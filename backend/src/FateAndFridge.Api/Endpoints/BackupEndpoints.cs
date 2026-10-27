using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Http.Features;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// One backup row on the wire (docs/API.md §18). <c>kind</c> and <c>status</c>
/// are the snake_case storage values (<c>manual</c>/<c>safety</c>,
/// <c>pending</c>/<c>completed</c>/<c>failed</c>/<c>restored</c>/<c>deleted</c>);
/// <c>checksum</c> is the lowercase SHA-256 of the encrypted file and is empty
/// until the create completes.
/// </summary>
public sealed record BackupResponse(
    long Id,
    string Filename,
    string Kind,
    string Status,
    string AppVersion,
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    string Checksum,
    string? Error)
{
    public static BackupResponse From(BackupMetadata backup) => new(
        backup.Id,
        backup.Filename,
        KindText(backup.Kind),
        StatusText(backup.Status),
        backup.AppVersion,
        backup.SchemaVersion,
        backup.CreatedAt,
        backup.SizeBytes,
        backup.Checksum,
        backup.Error);

    internal static string KindText(BackupKind kind) => kind switch
    {
        BackupKind.Manual => "manual",
        BackupKind.Safety => "safety",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown backup kind."),
    };

    internal static string StatusText(BackupStatus status) => status switch
    {
        BackupStatus.Pending => "pending",
        BackupStatus.Completed => "completed",
        BackupStatus.Failed => "failed",
        BackupStatus.Restored => "restored",
        BackupStatus.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown backup status."),
    };
}

/// <summary>
/// The <c>202 Accepted</c> body of a restore (docs/API.md §18). <c>backup_id</c>
/// is present when a stored archive was chosen and null for an upload; the
/// archive's file name is always reported, together with the safety backup that
/// was created before anything was touched.
/// </summary>
public sealed record RestoreResponse(
    string RestoreId,
    long? BackupId,
    string Filename,
    string SafetyBackupFilename);

/// <summary>
/// Admin-only backup routes (docs/M13_PLAN.md §2.1–§2.2, docs/API.md §18):
/// list, create, download, delete, and restore. Every route is
/// <c>RequireAdmin()</c> on the existing <c>admin</c> role (and the single-user
/// system actor); no eighth permission key is introduced. Recovery-key export,
/// the clean-install restore route, and the UI are later issues.
/// </summary>
public static class BackupEndpoints
{
    /// <summary>The exact typed confirmation for deleting one archive.</summary>
    public const string DeleteConfirmation = "DELETE";

    /// <summary>The exact typed confirmation for restoring one archive.</summary>
    public const string RestoreConfirmation = "RESTORE";

    private const string BackupsPath = "/api/v1/admin/backups";

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(BackupsPath).WithTags("Admin");

        group.MapGet("", ListAsync).RequireAdmin();
        group.MapPost("", CreateAsync).RequireAdmin();
        group.MapGet("/{id:long}/download", DownloadAsync).RequireAdmin();
        group.MapDelete("/{id:long}", DeleteAsync).RequireAdmin();
        group.MapPost("/restore", RestoreAsync).RequireAdmin();

        return app;
    }

    /// <summary>Newest-first list of every backup row, including failed/deleted ones.</summary>
    private static async Task<IResult> ListAsync(
        BackupService backups,
        CancellationToken cancellationToken)
    {
        var rows = await backups.ListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(rows.Select(BackupResponse.From).ToArray());
    }

    /// <summary>
    /// Creates one manual backup. The reason is required (the shared
    /// dangerous-action rules) and is written to the <c>backup.created</c> audit
    /// row. The dump and encryption run synchronously inside the request, so a
    /// success is <c>201 Created</c> with the row already <c>completed</c>
    /// (docs/API.md §18).
    /// </summary>
    private static async Task<IResult> CreateAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        BackupService backups,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        string? reason;
        using (var document = body.Document!)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidBody();
            }

            reason = ReadString(document.RootElement, "reason");
        }

        var validation = DangerousActionGuard.Validate(reason, confirmation: null, expectedConfirmation: null);
        if (validation is DangerousActionValidation.Failure validationFailure)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                validationFailure.Code,
                validationFailure.Message,
                validationFailure.ToErrorDetails());
        }

        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var outcome = await backups.CreateAsync(
                ((DangerousActionValidation.Success)validation).Reason,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            BackupCreateOutcome.Success success => Results.Json(
                BackupResponse.From(success.Backup),
                statusCode: StatusCodes.Status201Created),
            BackupCreateOutcome.Failure failure =>
                ApiResults.Error(failure.StatusCode, failure.Code, failure.Message),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>Streams the encrypted archive as an attachment.</summary>
    private static async Task<IResult> DownloadAsync(
        long id,
        BackupService backups,
        CancellationToken cancellationToken)
    {
        var outcome = await backups.OpenDownloadAsync(id, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            BackupDownloadOutcome.Success success => Results.File(
                success.Path,
                contentType: "application/octet-stream",
                fileDownloadName: success.Filename,
                enableRangeProcessing: true),
            BackupDownloadOutcome.Failure failure =>
                ApiResults.Error(failure.StatusCode, failure.Code, failure.Message),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// Deletes one archive after the shared dangerous-action guard accepts the
    /// reason and the exact <c>DELETE</c> confirmation.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        BackupService backups,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        string? reason;
        string? confirmation;
        using (var document = body.Document!)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return InvalidBody();
            }

            reason = ReadString(document.RootElement, "reason");
            confirmation = ReadString(document.RootElement, "confirmation");
        }

        var validation = DangerousActionGuard.Validate(reason, confirmation, DeleteConfirmation);
        if (validation is DangerousActionValidation.Failure validationFailure)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                validationFailure.Code,
                validationFailure.Message,
                validationFailure.ToErrorDetails());
        }

        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var outcome = await backups.DeleteAsync(
                id,
                ((DangerousActionValidation.Success)validation).Reason,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            BackupDeleteOutcome.Success success => Results.Ok(BackupResponse.From(success.Backup)),
            BackupDeleteOutcome.Failure failure =>
                ApiResults.Error(failure.StatusCode, failure.Code, failure.Message),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// Starts one restore (docs/M13_PLAN.md §1.9, docs/API.md §18). Accepts
    /// either JSON <c>{ backup_id, reason, confirmation }</c> or a
    /// <c>multipart/form-data</c> upload (<c>file</c>, <c>reason</c>,
    /// <c>confirmation</c>), requires the typed <c>RESTORE</c> confirmation, and
    /// validates the archive plus creates the safety backup **before** the
    /// <c>202</c>. The rest of the restore (maintenance mode, <c>pg_restore</c>,
    /// session revocation, audit, file swap, marker, restart) runs in the
    /// background.
    /// </summary>
    private static async Task<IResult> RestoreAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RestoreService restores,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Only this route accepts a 2 GB archive; every other route keeps the
        // default (and the 5 MiB multipart cap used by recipe images).
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = RestoreRequestReader.MaximumBodyBytes;
        }

        var restoreId = restores.NewRestoreId();
        var uploadPath = RestoreService.UploadPath(configuration, restoreId);

        var read = await RestoreRequestReader
            .ReadAsync(context, uploadPath, cancellationToken)
            .ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            RemoveRestoreDirectory(configuration, restoreId);
            return read.Error!;
        }

        var body = read.Body!;
        if (!string.IsNullOrWhiteSpace(body.RecoveryKey))
        {
            // The Admin route always uses this device's key file; the pasted key
            // belongs to the pre-setup clean-install route only.
            RemoveRestoreDirectory(configuration, restoreId);
            return ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["recovery_key"] =
                [
                    "A supplied recovery key is only accepted by the pre-setup restore route; " +
                    "this route uses the device's recovery key file.",
                ],
            });
        }

        var validation = DangerousActionGuard.Validate(
            body.Reason,
            body.Confirmation,
            RestoreConfirmation);
        if (validation is DangerousActionValidation.Failure validationFailure)
        {
            RemoveRestoreDirectory(configuration, restoreId);
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                validationFailure.Code,
                validationFailure.Message,
                validationFailure.ToErrorDetails());
        }

        RestoreSource source;
        if (body.Upload is not null && body.BackupId is not null)
        {
            RemoveRestoreDirectory(configuration, restoreId);
            return ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["body"] = ["Provide either backup_id or a file upload, not both."],
            });
        }

        if (body.Upload is not null)
        {
            source = body.Upload;
        }
        else if (body.BackupId is not null)
        {
            source = new RestoreSource.StoredBackup(body.BackupId.Value);
        }
        else
        {
            RemoveRestoreDirectory(configuration, restoreId);
            return ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["body"] = ["Provide either backup_id or a file upload."],
            });
        }

        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            RemoveRestoreDirectory(configuration, restoreId);
            return actorError;
        }

        var outcome = await restores.BeginAsync(
                source,
                ((DangerousActionValidation.Success)validation).Reason,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                restoreId,
                cancellationToken)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case RestoreStartOutcome.Accepted accepted:
                restores.StartInBackground(accepted.Operation);
                return Results.Json(
                    new RestoreResponse(
                        accepted.Operation.RestoreId,
                        accepted.Operation.BackupId,
                        accepted.Operation.BackupFilename,
                        accepted.Operation.SafetyBackupFilename),
                    statusCode: StatusCodes.Status202Accepted);

            case RestoreStartOutcome.Failure failure:
                RemoveRestoreDirectory(configuration, restoreId);
                return ApiResults.Error(failure.StatusCode, failure.Code, failure.Message);

            default:
                RemoveRestoreDirectory(configuration, restoreId);
                return ApiResults.Error(
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.RestoreFailed,
                    "The restore could not be started.");
        }
    }

    /// <summary>
    /// Removes one restore's working directory. Shared with the pre-setup
    /// restore route, which uses the same staging area.
    /// </summary>
    internal static void RemoveRestoreDirectory(IConfiguration configuration, string restoreId)
    {
        try
        {
            var directory = RestoreService.RestoreDirectory(configuration, restoreId);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover working directory is harmless: it is not a live path
            // and the next restore uses a fresh id.
        }
    }

    private static IResult InvalidBody() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });

    private static string? ReadString(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
