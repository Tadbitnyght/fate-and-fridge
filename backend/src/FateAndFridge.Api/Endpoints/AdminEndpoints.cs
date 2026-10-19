using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// One row of the admin audit timeline (docs/API.md §16, docs/M12_PLAN.md §2.2).
/// <c>before</c>, <c>after</c>, and <c>metadata</c> are the stored JSON documents
/// (or null), embedded as JSON values rather than escaped strings.
/// </summary>
public sealed record AuditLogItemResponse(
    long Id,
    string Source,
    DateTimeOffset RecordedAt,
    long? ActorUserId,
    string? ActorUsername,
    string EventType,
    string EntityType,
    string? EntityId,
    string Summary,
    string? Reason,
    string? IpAddress,
    JsonElement? Before,
    JsonElement? After,
    JsonElement? Metadata)
{
    public static AuditLogItemResponse From(AuditLogItemRecord item) => new(
        item.Id,
        item.Source,
        item.RecordedAt,
        item.ActorUserId,
        item.ActorUsername,
        item.EventType,
        item.EntityType,
        item.EntityId,
        item.Summary,
        item.Reason,
        item.IpAddress,
        ParseJson(item.BeforeJson),
        ParseJson(item.AfterJson),
        ParseJson(item.MetadataJson));

    /// <summary>
    /// Embeds a stored JSONB document as a JSON value. The column is jsonb, so
    /// the text is always valid JSON; a defensive parse failure yields null
    /// rather than a 500.
    /// </summary>
    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One newest-first page of the audit timeline plus its next cursor.</summary>
public sealed record AuditLogPageResponse(
    IReadOnlyList<AuditLogItemResponse> Items,
    string? NextCursor)
{
    public static AuditLogPageResponse From(AuditLogPageRecord page) =>
        new(page.Items.Select(AuditLogItemResponse.From).ToArray(), page.NextCursor);
}

/// <summary>The result of one confirmed retention prune (docs/API.md §16).</summary>
public sealed record AuditPruneResponse(
    int DeletedCount,
    DateTimeOffset Cutoff,
    int RetentionDays);

/// <summary>
/// Admin-only administration routes (docs/M12_PLAN.md §2.1, §2.2). Read access to
/// the audit log is the existing <c>admin</c> role — no new permission key
/// (docs/M12_PLAN.md §1.1).
/// </summary>
public static class AdminEndpoints
{
    /// <summary>The exact typed confirmation for the audit retention prune.</summary>
    public const string PruneConfirmation = "PRUNE";

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").WithTags("Admin");

        group.MapGet("/audit", GetAuditAsync).RequireAdmin();
        group.MapGet("/audit/export", ExportAuditAsync).RequireAdmin();
        group.MapPost("/audit/prune", PruneAuditAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> GetAuditAsync(
        HttpContext context,
        AuditLogQueryService auditLog,
        CancellationToken cancellationToken)
    {
        var request = ReadRequest(context);

        var outcome = await auditLog.QueryAsync(request, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            AuditLogQueryOutcome.Success success =>
                Results.Ok(AuditLogPageResponse.From(success.Page)),
            AuditLogQueryOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// Downloads the current filtered audit view as CSV or JSON (docs/API.md
    /// §16, docs/M12_PLAN.md §1.7). Paging is ignored: an export is the whole
    /// filtered view, capped by <see cref="AuditLogQueryService.MaxExportRows"/>.
    /// </summary>
    private static async Task<IResult> ExportAuditAsync(
        HttpContext context,
        AuditLogQueryService auditLog,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        var request = ReadRequest(context, includePaging: false);
        var outcome = await auditLog
            .ExportAsync(request, ReadQuery(context, "format"), cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            AuditLogExportOutcome.Success success
                when success.Format == AuditLogQueryService.CsvFormat =>
                Results.File(
                    Encoding.UTF8.GetBytes(AuditLogCsv.Write(success.Items)),
                    "text/csv; charset=utf-8",
                    "audit-log.csv"),
            AuditLogExportOutcome.Success success => Results.File(
                JsonSerializer.SerializeToUtf8Bytes(
                    success.Items.Select(AuditLogItemResponse.From).ToArray(),
                    jsonOptions.Value.SerializerOptions),
                "application/json; charset=utf-8",
                "audit-log.json"),
            AuditLogExportOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    /// <summary>
    /// The explicit, age-based audit retention prune (docs/API.md §16,
    /// docs/M12_PLAN.md §1.8): the shared dangerous-action guard verifies the
    /// reason and the exact <c>PRUNE</c> confirmation, then only
    /// <c>audit_events</c> rows older than the configured window are deleted and
    /// an <c>audit.pruned</c> row records the counts.
    /// </summary>
    private static async Task<IResult> PruneAuditAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        SecuritySettingsService securitySettings,
        AuditLogQueryService auditLog,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
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
                return ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["body"] = ["The request body must be a valid JSON object."],
                });
            }

            reason = ReadString(document.RootElement, "reason");
            confirmation = ReadString(document.RootElement, "confirmation");
        }

        var validation = DangerousActionGuard.Validate(reason, confirmation, PruneConfirmation);
        if (validation is DangerousActionValidation.Failure failure)
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                failure.Code,
                failure.Message,
                failure.ToErrorDetails());
        }

        var trimmedReason = ((DangerousActionValidation.Success)validation).Reason;

        var settings = await securitySettings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (settings.AuditRetentionDays == 0)
        {
            return ApiResults.ValidationError(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["audit_retention_days"] = [
                    "Audit retention is set to keep forever, so there is nothing to prune."],
            });
        }

        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var cutoff = timeProvider.GetUtcNow().AddDays(-settings.AuditRetentionDays);
        var result = await auditLog.PruneAsync(
                cutoff,
                settings.AuditRetentionDays,
                trimmedReason,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new AuditPruneResponse(
            result.DeletedCount,
            result.Cutoff,
            result.RetentionDays));
    }

    private static AuditLogQueryRequest ReadRequest(HttpContext context, bool includePaging = true) => new(
        ReadQuery(context, "event_type"),
        ReadQuery(context, "entity_type"),
        ReadQuery(context, "actor_user_id"),
        ReadQuery(context, "source"),
        ReadQuery(context, "from"),
        ReadQuery(context, "to"),
        includePaging ? ReadQuery(context, "limit") : null,
        includePaging ? ReadQuery(context, "cursor") : null);

    private static string? ReadString(JsonElement root, string field) =>
        root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadQuery(HttpContext context, string field)
    {
        if (!context.Request.Query.TryGetValue(field, out var values) || values.Count == 0)
        {
            return null;
        }

        return values[^1];
    }
}
