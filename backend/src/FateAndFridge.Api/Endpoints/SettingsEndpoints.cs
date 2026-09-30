using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
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

        return app;
    }

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
        HouseholdSettingsService settingsService,
        CancellationToken cancellationToken)
    {
        JsonElement body;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
            body = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ApiResults.ValidationError(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body must be valid JSON."],
            });
        }

        var validation = HouseholdSettingsPatchValidator.Validate(body);
        if (validation.Patch is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var settings = await settingsService.ApplyPatchAsync(validation.Patch, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        ResponseCache.NoStore(context);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return ApiResults.ValidationError(new Dictionary<string, string[]>
            {
                ["body"] = ["The request body must be a valid JSON object."],
            });
        }

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
