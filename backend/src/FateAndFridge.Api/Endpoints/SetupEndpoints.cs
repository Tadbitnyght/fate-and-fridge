using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Backup;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Setup completion response (docs/API.md §21): the recovery key is returned
/// exactly once here; <see cref="Session"/> is present only in multiuser mode.
/// </summary>
public sealed record SetupCompleteResponse(
    string AuthMode,
    string RecoveryKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuthSessionResponse? Session);

/// <summary>
/// The <c>202 Accepted</c> body of a clean-install restore (docs/API.md §21):
/// the restore id, the archive's file name, and the safety archive created
/// before anything was touched. There is no <c>backup_id</c> before setup — no
/// backup catalogue exists yet.
/// </summary>
public sealed record SetupRestoreResponse(
    string RestoreId,
    string Filename,
    string SafetyBackupFilename);

/// <summary>Setup routes (docs/API.md §21). Public while setup is incomplete.</summary>
public static class SetupEndpoints
{
    /// <summary>
    /// The exact typed confirmation for a clean-install restore
    /// (docs/API.md §21): with no session and no Admin, the archive plus its
    /// recovery key are the credential and this typed word is the
    /// acknowledgement.
    /// </summary>
    public const string RestoreConfirmation = "RESTORE";

    private const string AlreadyCompletedMessage = "Setup has already been completed.";

    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/setup").WithTags("Setup");

        group.MapGet("/status", async (SetupStateService setupState, CancellationToken cancellationToken) =>
            Results.Ok(await setupState.GetStatusAsync(cancellationToken)));

        // Public, single-shot, atomic (docs/API.md §21): 201 on success, 409 on
        // repeat, 400 with field errors on invalid input.
        group.MapPost("/complete", CompleteSetupAsync);

        // Public **only before setup** (docs/M13_PLAN.md §1.10): the pre-setup
        // gate already exempts /api/v1/setup/*, so this route is unreachable as
        // a pre-setup escape hatch once setup is complete — it answers 409.
        group.MapPost("/restore", RestoreFromBackupAsync);

        return app;
    }

    private static async Task<IResult> CompleteSetupAsync(
        HttpContext context,
        SetupStateService setupState,
        SetupCompletionService setupCompletion,
        PermissionService permissionService,
        IAntiforgery antiforgery,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        // The response carries the one-time recovery key: never cache it.
        ResponseCache.NoStore(context);

        // Repeats return 409 before body parsing/validation so a malformed body
        // on an already-configured instance still yields SETUP_ALREADY_COMPLETED.
        // The authoritative, race-safe check happens inside the setup
        // transaction under the advisory lock.
        if (await setupState.IsSetupCompletedAsync(cancellationToken))
        {
            return AlreadyCompleted();
        }

        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        var document = body.Document!;
        using (document)
        {
            var validation = SetupCompletionValidator.Validate(document.RootElement);
            if (validation.Request is null)
            {
                return ApiResults.ValidationError(validation.FieldErrors);
            }

            // Enabling the allowlist during setup must not lock out the device
            // performing setup (docs/API.md §21): the resolved client address
            // has to be covered by the ranges; loopback is always allowed.
            if (validation.Request.IpAllowlistEnabled)
            {
                var client = ClientAddress.Normalize(context.Connection.RemoteIpAddress);
                if (client is null
                    || !(ClientAddress.IsLoopback(client)
                        || ClientAddress.IsInAnyRange(client, validation.Request.IpAllowlistRanges)))
                {
                    return ApiResults.ValidationError(new Dictionary<string, string[]>
                    {
                        ["ip_allowlist.ranges"] = [
                            "The caller's address must be covered by the allowlist ranges to avoid locking out this device."],
                    });
                }
            }

            var result = await setupCompletion.CompleteAsync(
                validation.Request,
                context.Connection.RemoteIpAddress?.ToString(),
                UserAgent(context),
                cancellationToken);

            return result switch
            {
                SetupCompletionResult.AlreadyCompleted => AlreadyCompleted(),
                SetupCompletionResult.RecoveryKeyUnusable unusable => ApiResults.Error(
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.InternalError,
                    unusable.Message),
                SetupCompletionResult.Success success => await SetupCompletedAsync(
                    context,
                    success,
                    permissionService,
                    antiforgery,
                    jsonOptions,
                    cancellationToken),
                _ => throw new InvalidOperationException("Unknown setup completion result."),
            };
        }
    }

    /// <summary>
    /// Restores an uploaded archive **before** setup completes
    /// (docs/M13_PLAN.md §1.10, docs/API.md §21). No session and no Admin exist
    /// yet, so the archive plus the pasted recovery key are the credential and
    /// the typed <c>RESTORE</c> is the acknowledgement. The shared M13-04
    /// sequence does the work: format/integrity/checksum/schema validation, then
    /// the mandatory safety backup, then <c>202</c> and a background restore that
    /// stops the process so the container restarts into the restored state.
    /// </summary>
    private static async Task<IResult> RestoreFromBackupAsync(
        HttpContext context,
        SetupStateService setupState,
        RestoreService restores,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Like the Admin restore route, only this route raises the body limit to
        // the 2 GB archive cap (plus multipart slack).
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = RestoreRequestReader.MaximumBodyBytes;
        }

        // Repeats answer 409 before the body is parsed: once setup is complete
        // this route no longer restores anything, and the Admin route owns it.
        if (await setupState.IsSetupCompletedAsync(cancellationToken))
        {
            return AlreadyCompleted();
        }

        if (!context.Request.HasFormContentType)
        {
            return ApiResults.Error(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.UnsupportedMediaType,
                "A restore archive must be uploaded as multipart/form-data.");
        }

        var restoreId = restores.NewRestoreId();
        var uploadPath = RestoreService.UploadPath(configuration, restoreId);

        var read = await RestoreRequestReader
            .ReadAsync(context, uploadPath, cancellationToken)
            .ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
            return read.Error!;
        }

        var body = read.Body!;
        if (body.Upload is null || body.BackupId is not null)
        {
            BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
            return ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["file"] = ["A backup archive file is required."],
            });
        }

        // The key's shape is checked before the archive is opened, so a mistyped
        // paste is a field error instead of an integrity failure.
        var keyText = body.RecoveryKey?.Trim();
        if (string.IsNullOrEmpty(keyText))
        {
            BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
            return InvalidRecoveryKey(
                "Paste the recovery key that was shown when this backup was created.");
        }

        byte[] recoveryKey;
        try
        {
            recoveryKey = Base64Url.DecodeFromChars(keyText);
        }
        catch (FormatException)
        {
            BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
            return InvalidRecoveryKey("The recovery key is not valid base64url text.");
        }

        if (recoveryKey.Length != BackupArchiveFormat.RecoveryKeySize)
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
            BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
            return InvalidRecoveryKey(
                $"The recovery key must decode to exactly {BackupArchiveFormat.RecoveryKeySize} bytes.");
        }

        var confirmation = DangerousActionGuard.Validate(
            RestoreService.SetupRestoreReason,
            body.Confirmation,
            RestoreConfirmation);
        if (confirmation is DangerousActionValidation.Failure confirmationFailure)
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
            BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                confirmationFailure.Code,
                confirmationFailure.Message,
                confirmationFailure.ToErrorDetails());
        }

        // The response carries no secret, but it must not be cached either.
        ResponseCache.NoStore(context);

        var outcome = await restores
            .BeginAsync(
                body.Upload,
                new RestoreRequester.Setup(
                    recoveryKey,
                    CalendarEndpoints.RemoteIpAddress(context)),
                restoreId,
                cancellationToken)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case RestoreStartOutcome.Accepted accepted:
                // The background phase owns the key from here and zeroes it when
                // it finishes.
                restores.StartInBackground(accepted.Operation);
                return Results.Json(
                    new SetupRestoreResponse(
                        accepted.Operation.RestoreId,
                        accepted.Operation.BackupFilename,
                        accepted.Operation.SafetyBackupFilename),
                    statusCode: StatusCodes.Status202Accepted);

            case RestoreStartOutcome.Failure failure:
                CryptographicOperations.ZeroMemory(recoveryKey);
                BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
                return ApiResults.Error(failure.StatusCode, failure.Code, failure.Message);

            default:
                CryptographicOperations.ZeroMemory(recoveryKey);
                BackupEndpoints.RemoveRestoreDirectory(configuration, restoreId);
                return ApiResults.Error(
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.RestoreFailed,
                    "The restore could not be started.");
        }
    }

    private static IResult InvalidRecoveryKey(string message) =>
        ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["recovery_key"] = [message],
        });

    private static IResult AlreadyCompleted() =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.SetupAlreadyCompleted,
            AlreadyCompletedMessage);

    private static async Task<IResult> SetupCompletedAsync(
        HttpContext context,
        SetupCompletionResult.Success success,
        PermissionService permissionService,
        IAntiforgery antiforgery,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        AuthSessionResponse? session = null;
        if (success.Admin is { } admin
            && success.Session is { } userSession
            && success.RawSessionToken is { } rawToken)
        {
            // Same sign-in as a successful login: cookie, principal, CSRF pair.
            SessionSignIn.Apply(context, admin, userSession, rawToken, antiforgery);
            session = await SessionPayloads.ForUserAsync(permissionService, admin, cancellationToken);
        }

        var payload = new SetupCompleteResponse(
            EnumTextConverters.AuthModeToText(success.AuthMode),
            success.RecoveryKey,
            session);

        return Results.Json(
            payload,
            jsonOptions.Value.SerializerOptions,
            statusCode: StatusCodes.Status201Created);
    }

    private static string? UserAgent(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }
}
