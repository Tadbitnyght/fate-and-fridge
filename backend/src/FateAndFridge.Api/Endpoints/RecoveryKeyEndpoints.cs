using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Admin-only recovery-key export (docs/M13_PLAN.md §1.11, §2.2;
/// docs/SECURITY.md §11, docs/DATABASE.md §11): the one confirmed download of
/// the household key file.
/// </summary>
/// <remarks>
/// <para>
/// The route is <c>RequireAdmin()</c> on the existing <c>admin</c> role (and the
/// single-user system actor); no eighth permission key is introduced. The
/// caller must also pass the shared dangerous-action guard with the exact typed
/// <c>EXPORT</c> confirmation, and the attempt is audited as
/// <c>recovery_key.exported</c>.
/// </para>
/// <para>
/// The key is only ever returned as an <c>attachment</c> download — never as a
/// JSON body, never inline, never in an audit payload, log entry, or error
/// message. The only other exposure point is the single display at setup
/// completion.
/// </para>
/// </remarks>
public static class RecoveryKeyEndpoints
{
    /// <summary>The exact typed confirmation for exporting the recovery key.</summary>
    public const string ExportConfirmation = "EXPORT";

    /// <summary>The file name the key is downloaded under.</summary>
    public const string ExportFileName = RecoveryKeyStore.FileName;

    private const string ExportPath = "/api/v1/admin/recovery-key/export";

    public static IEndpointRouteBuilder MapRecoveryKeyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(ExportPath, ExportAsync)
            .WithTags("Admin")
            .RequireAdmin();

        return app;
    }

    /// <summary>
    /// Answers the key file as a <c>text/plain</c> attachment after the guarded
    /// request passes. A missing or unusable key file is
    /// <c>409 BACKUP_KEY_MISSING</c>; the export never creates a key.
    /// </summary>
    private static async Task<IResult> ExportAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RecoveryKeyStore recoveryKeyStore,
        IAuditService auditService,
        AppDbContext dbContext,
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

        var validation = DangerousActionGuard.Validate(reason, confirmation, ExportConfirmation);
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

        string? keyText;
        try
        {
            keyText = await recoveryKeyStore.TryReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            // An unusable file is treated like a missing one: never export a
            // value the household cannot use, and never echo the file content.
            keyText = null;
        }

        if (keyText is null)
        {
            return ApiResults.Error(
                StatusCodes.Status409Conflict,
                ErrorCodes.BackupKeyMissing,
                "The recovery key file is missing or unusable. Complete setup to create it.");
        }

        // Audit before the key leaves the process: a failed audit write fails
        // the request instead of producing an unaudited download. The payload
        // carries only the fact and the file name, never the key.
        await auditService.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.RecoveryKeyExported,
                    AuditEntityTypes.RecoveryKey,
                    Reason: ((DangerousActionValidation.Success)validation).Reason,
                    IpAddress: CalendarEndpoints.RemoteIpAddress(context),
                    ActorUserId: actorUserId,
                    Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["filename"] = ExportFileName,
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Results.File(
            Encoding.UTF8.GetBytes(keyText),
            contentType: "text/plain",
            fileDownloadName: ExportFileName);
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
