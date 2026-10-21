using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

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
/// Admin-only backup routes (docs/M13_PLAN.md §2.1–§2.2, docs/API.md §18):
/// list, create, download, and delete. Every route is <c>RequireAdmin()</c> on
/// the existing <c>admin</c> role (and the single-user system actor); no eighth
/// permission key is introduced. Restore, key export, and the UI are later
/// issues.
/// </summary>
public static class BackupEndpoints
{
    /// <summary>The exact typed confirmation for deleting one archive.</summary>
    public const string DeleteConfirmation = "DELETE";

    private const string BackupsPath = "/api/v1/admin/backups";

    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(BackupsPath).WithTags("Admin");

        group.MapGet("", ListAsync).RequireAdmin();
        group.MapPost("", CreateAsync).RequireAdmin();
        group.MapGet("/{id:long}/download", DownloadAsync).RequireAdmin();
        group.MapDelete("/{id:long}", DeleteAsync).RequireAdmin();

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
