using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Antiforgery;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Household settings routes (docs/API.md §21). Reads require any
/// authenticated actor; writes (including the auth-mode switch) require the
/// system actor or an Admin. Access is enforced by the M1-09 permission filters.
/// </summary>
public static class SettingsEndpoints
{
    private const string DowngradeMessage = "Multiuser to single-user conversion is not supported.";

    private const string AlreadyMultiuserMessage = "The household already uses multiuser mode.";

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/settings").WithTags("Settings");

        group.MapGet("/household", GetHouseholdSettingsAsync).RequireAuthenticated();
        group.MapPatch("/household", PatchHouseholdSettingsAsync).RequireAdmin();
        group.MapPost("/auth-mode", SwitchAuthModeAsync).RequireAdmin();

        // M1-11: Admin-only security settings (IP allowlist and trusted proxies).
        group.MapGet("/security", GetSecuritySettingsAsync).RequireAdmin();
        group.MapPatch("/security", PatchSecuritySettingsAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> GetSecuritySettingsAsync(
        SecuritySettingsService securitySettings,
        CancellationToken cancellationToken) =>
        Results.Ok(await securitySettings.GetAsync(cancellationToken));

    /// <summary>
    /// Updates the security settings (docs/API.md §21). Enabling the allowlist
    /// requires <c>confirmed: true</c>, at least one range, and coverage of the
    /// caller's resolved client address; disabling needs no confirmation.
    /// Changes apply to the next request.
    /// </summary>
    private static async Task<IResult> PatchSecuritySettingsAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        SecuritySettingsService securitySettings,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadObjectAsync(context, cancellationToken);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        var root = parsed.Root;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        bool? enabled = null;
        if (root.TryGetProperty("ip_allowlist_enabled", out var enabledElement))
        {
            if (enabledElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                enabled = enabledElement.GetBoolean();
            }
            else
            {
                errors["ip_allowlist_enabled"] = ["ip_allowlist_enabled must be a boolean."];
            }
        }

        IReadOnlyList<string>? ranges = null;
        if (root.TryGetProperty("ip_allowlist_ranges", out var rangesElement))
        {
            ranges = ReadNormalizedRanges(rangesElement, "ip_allowlist_ranges", errors);
        }

        IReadOnlyList<string>? trustedProxies = null;
        if (root.TryGetProperty("trusted_proxies", out var proxiesElement))
        {
            trustedProxies = ReadNormalizedRanges(proxiesElement, "trusted_proxies", errors);
        }

        int? retentionDays = null;
        if (root.TryGetProperty("audit_retention_days", out var retentionElement))
        {
            if (retentionElement.ValueKind == JsonValueKind.Number
                && retentionElement.TryGetInt32(out var parsedRetention)
                && SecuritySettingsService.IsValidAuditRetentionDays(parsedRetention))
            {
                retentionDays = parsedRetention;
            }
            else
            {
                errors["audit_retention_days"] = [
                    "audit_retention_days must be 0 (keep forever) or an integer between " +
                    $"{SecuritySettingsService.MinAuditRetentionDays} and " +
                    $"{SecuritySettingsService.MaxAuditRetentionDays}."];
            }
        }

        bool? confirmed = null;
        if (root.TryGetProperty("confirmed", out var confirmedElement))
        {
            if (confirmedElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                confirmed = confirmedElement.GetBoolean();
            }
            else
            {
                errors["confirmed"] = ["confirmed must be a boolean."];
            }
        }

        if (enabled is null && ranges is null && trustedProxies is null && retentionDays is null)
        {
            errors["body"] = [
                "At least one of ip_allowlist_enabled, ip_allowlist_ranges, trusted_proxies, or audit_retention_days must be provided."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var current = await securitySettings.GetAsync(cancellationToken);
        var resultingEnabled = enabled ?? current.IpAllowlistEnabled;
        var resultingRanges = ranges ?? current.IpAllowlistRanges;
        var resultingProxies = trustedProxies ?? current.TrustedProxies;
        var resultingRetention = retentionDays ?? current.AuditRetentionDays;

        if (resultingEnabled)
        {
            if (!current.IpAllowlistEnabled && confirmed != true)
            {
                errors["confirmed"] = ["Enabling the IP allowlist requires confirmed: true."];
            }

            if (resultingRanges.Count == 0)
            {
                errors["ip_allowlist_ranges"] = [
                    "At least one range is required when the allowlist is enabled."];
            }
            else
            {
                var client = ClientAddress.Normalize(context.Connection.RemoteIpAddress);
                if (client is null
                    || !(ClientAddress.IsLoopback(client)
                        || ClientAddress.IsInAnyRange(client, resultingRanges)))
                {
                    errors["ip_allowlist_ranges"] = [
                        "The caller's address must be covered by the allowlist ranges to avoid locking out this session."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        // Resolve the actor before the write so the audit row can never be
        // misattributed to the single-user system actor (fail closed).
        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var updated = new SecuritySettings(resultingEnabled, resultingRanges, resultingProxies, resultingRetention);
        await securitySettings.SaveAsync(
            updated,
            actorUserId,
            CalendarEndpoints.RemoteIpAddress(context),
            cancellationToken);

        await liveSync.SettingsUpdatedAsync(
            LiveSyncPublisherExtensions.SettingsEntity,
            actorUserId,
            cancellationToken: cancellationToken);
        return Results.Ok(updated);
    }

    private static IReadOnlyList<string>? ReadNormalizedRanges(
        JsonElement element,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            errors[field] = [$"{field} must be an array of IPv4/IPv6 addresses or CIDR ranges."];
            return null;
        }

        var normalized = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !ClientAddress.TryNormalizeRange(item.GetString(), out var cidr))
            {
                errors[field] = [$"Each {field} entry must be a valid IPv4/IPv6 address or CIDR range."];
                return null;
            }

            normalized.Add(cidr);
        }

        return normalized;
    }

    private static async Task<(JsonElement Root, IResult? Error)> ReadObjectAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return (default, body.Error);
        }

        using var document = body.Document!;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (default, BodyError());
        }

        return (document.RootElement.Clone(), null);
    }

    private static IResult BodyError() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });

    private static async Task<IResult> GetHouseholdSettingsAsync(
        HouseholdSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The household settings row is missing although setup is complete.");

        return Results.Ok(HouseholdSettingsResponse.From(settings));
    }

    private static async Task<IResult> PatchHouseholdSettingsAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        HouseholdSettingsService settingsService,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        JsonElement root;
        using (var document = body.Document!)
        {
            root = document.RootElement.Clone();
        }

        var validation = HouseholdSettingsPatchValidator.Validate(root);
        if (validation.Patch is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        // The spacing fields are coupled (far >= near in docs/M6_PLAN.md §1.3),
        // so the cross-field check needs the row the patch is applied to.
        var current = await settingsService.GetAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "The household settings row is missing although setup is complete.");
        var crossFieldErrors = HouseholdSettingsPatchValidator.ValidateAgainstCurrent(
            validation.Patch,
            current);
        if (crossFieldErrors.Count > 0)
        {
            return ApiResults.ValidationError(crossFieldErrors);
        }

        // Resolve the actor before the write so the audit row can never be
        // misattributed to the single-user system actor (fail closed).
        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var settings = await settingsService.ApplyPatchAsync(
            validation.Patch,
            actorUserId,
            CalendarEndpoints.RemoteIpAddress(context),
            cancellationToken);

        await liveSync.SettingsUpdatedAsync(
            LiveSyncPublisherExtensions.SettingsEntity,
            actorUserId,
            cancellationToken: cancellationToken);
        return Results.Ok(HouseholdSettingsResponse.From(settings));
    }

    /// <summary>
    /// Single-user → multiuser conversion (docs/API.md §21). Body
    /// <c>{ auth_mode?: "multiuser", admin: { username, password } }</c>;
    /// <c>auth_mode: "single_user"</c> returns
    /// <c>409 AUTH_MODE_DOWNGRADE_NOT_SUPPORTED</c>, and an already-multiuser
    /// household returns <c>409 AUTH_MODE_ALREADY_MULTIUSER</c>. On success the
    /// first Admin is created and signed in (same cookie/CSRF path as login).
    /// </summary>
    private static async Task<IResult> SwitchAuthModeAsync(
        HttpContext context,
        AuthModeService authModeService,
        HouseholdSettingsService householdSettings,
        PermissionService permissionService,
        IAntiforgery antiforgery,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        ResponseCache.NoStore(context);

        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        var document = body.Document!;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApiResults.ValidationError(new Dictionary<string, string[]>
                {
                    ["body"] = ["The request body must be a valid JSON object."],
                });
            }

            var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
            AuthMode? requestedMode = null;
            if (root.TryGetProperty("auth_mode", out var modeElement))
            {
                if (modeElement.ValueKind == JsonValueKind.String
                    && EnumTextConverters.TryAuthModeFromText(modeElement.GetString(), out var parsedMode))
                {
                    requestedMode = parsedMode;
                }
                else
                {
                    errors["auth_mode"] = ["auth_mode must be \"single_user\" or \"multiuser\"."];
                }
            }

            if (errors.Count > 0)
            {
                return ApiResults.ValidationError(errors);
            }

            if (requestedMode == AuthMode.SingleUser)
            {
                return ApiResults.Error(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.AuthModeDowngradeNotSupported,
                    DowngradeMessage);
            }

            // Repeats return 409 before the admin block is validated, so a body
            // without admin on an already-configured household still yields
            // AUTH_MODE_ALREADY_MULTIUSER. The authoritative check happens inside
            // the switch transaction under the advisory lock.
            var settings = await householdSettings.GetAsync(cancellationToken);
            if (settings?.AuthMode == AuthMode.Multiuser)
            {
                return ApiResults.Error(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.AuthModeAlreadyMultiuser,
                    AlreadyMultiuserMessage);
            }

            if (!root.TryGetProperty("admin", out var adminElement)
                || adminElement.ValueKind != JsonValueKind.Object)
            {
                errors["admin"] = ["admin is required to create the first Admin."];
                return ApiResults.ValidationError(errors);
            }

            var credentials = SetupCompletionValidator.ReadAdminCredentials(adminElement, errors);
            if (credentials is null)
            {
                return ApiResults.ValidationError(errors);
            }

            var result = await authModeService.SwitchToMultiuserAsync(
                credentials.Username,
                credentials.NormalizedUsername,
                credentials.Password,
                context.Connection.RemoteIpAddress?.ToString(),
                UserAgent(context),
                cancellationToken);

            if (result is AuthModeSwitchResult.Success)
            {
                // The mode switch invalidates every socket opened under the old
                // mode. In single-user mode those sockets are the cookie-less
                // system actor, and they must not keep receiving household events
                // once multiuser mode requires a session (docs/SECURITY.md §16).
                // They are aborted without session.revoked so each client
                // reconnects and authenticates under the new mode instead of
                // being signed out; the reconnect's full invalidation pass
                // reconciles anything missed (docs/M9_PLAN.md §1.8).
                await liveSync.DisconnectAllAsync(cancellationToken);

                // The caller is the single-user system actor: this route only ever
                // succeeds during the single→multiuser transition, and it returns
                // 409 AUTH_MODE_ALREADY_MULTIUSER before this point when the
                // household is already multiuser. There is therefore no session
                // user to attribute, so actor_user_id is intentionally null (the
                // system-actor envelope, docs/M9_PLAN.md §2.2).
                await liveSync.SettingsUpdatedAsync(
                    LiveSyncPublisherExtensions.SettingsEntity,
                    actorUserId: null,
                    cancellationToken: cancellationToken);
            }

            return result switch
            {
                AuthModeSwitchResult.Success success => await AuthModeSwitchedAsync(
                    context,
                    success,
                    permissionService,
                    antiforgery,
                    cancellationToken),
                AuthModeSwitchResult.AlreadyMultiuser => ApiResults.Error(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.AuthModeAlreadyMultiuser,
                    AlreadyMultiuserMessage),
                AuthModeSwitchResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
                _ => ApiResults.Error(
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.InternalError,
                    "The household settings row is missing although setup is complete."),
            };
        }
    }

    private static async Task<IResult> AuthModeSwitchedAsync(
        HttpContext context,
        AuthModeSwitchResult.Success success,
        PermissionService permissionService,
        IAntiforgery antiforgery,
        CancellationToken cancellationToken)
    {
        // Same sign-in as a successful login: cookie, principal, CSRF pair.
        SessionSignIn.Apply(context, success.Admin, success.Session, success.RawSessionToken, antiforgery);

        return Results.Ok(await SessionPayloads.ForUserAsync(permissionService, success.Admin, cancellationToken));
    }

    private static string? UserAgent(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }
}
