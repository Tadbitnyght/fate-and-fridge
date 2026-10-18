using System.Text.Json;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

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

/// <summary>
/// Admin-only administration routes (docs/M12_PLAN.md §2.1, §2.2). Read access to
/// the audit log is the existing <c>admin</c> role — no new permission key
/// (docs/M12_PLAN.md §1.1).
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").WithTags("Admin");

        group.MapGet("/audit", GetAuditAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> GetAuditAsync(
        HttpContext context,
        AuditLogQueryService auditLog,
        CancellationToken cancellationToken)
    {
        var request = new AuditLogQueryRequest(
            ReadQuery(context, "event_type"),
            ReadQuery(context, "entity_type"),
            ReadQuery(context, "actor_user_id"),
            ReadQuery(context, "source"),
            ReadQuery(context, "from"),
            ReadQuery(context, "to"),
            ReadQuery(context, "limit"),
            ReadQuery(context, "cursor"));

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

    private static string? ReadQuery(HttpContext context, string field)
    {
        if (!context.Request.Query.TryGetValue(field, out var values) || values.Count == 0)
        {
            return null;
        }

        return values[^1];
    }
}
