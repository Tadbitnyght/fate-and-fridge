using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Stable <c>audit_events.event_type</c> values for reference-data changes
/// (docs/DATABASE.md §10, docs/M2_PLAN.md §3 M2-02).
/// </summary>
/// <remarks>
/// The vocabulary is intentionally open: <c>event_type</c> and
/// <c>entity_type</c> are plain strings with no database CHECK constraint, so
/// M12 can add calendar/user/settings events without a service or schema
/// change. These constants are the canonical M2 reference-data set.
/// </remarks>
public static class AuditEventTypes
{
    public const string Created = "created";

    public const string Updated = "updated";

    public const string Retired = "retired";

    public const string Deleted = "deleted";

    public const string Merged = "merged";

    public const string FactorChanged = "factor_changed";

    public const string Reordered = "reordered";
}

/// <summary>
/// Stable <c>audit_events.entity_type</c> values for the M2 reference data
/// (docs/DATABASE.md §10).
/// </summary>
/// <remarks>
/// The vocabulary is intentionally open (see <see cref="AuditEventTypes"/>):
/// M12 extends both sets without service or schema changes; these constants
/// are the canonical M2 reference-data set.
/// </remarks>
public static class AuditEntityTypes
{
    public const string Department = "department";

    public const string Unit = "unit";

    public const string Ingredient = "ingredient";

    public const string Protein = "protein";

    public const string Tag = "tag";
}

/// <summary>
/// One <c>audit_events</c> row to persist (docs/DATABASE.md §10). The service
/// never accepts, requires, or derives password, token, session, or recovery-key
/// material: <see cref="BeforeJson"/>/<see cref="AfterJson"/> are explicit
/// caller-supplied JSON snapshots and <see cref="Metadata"/> holds only
/// event-specific details. Callers must keep secrets out of every value.
/// </summary>
public sealed record AuditEventEntry(
    string EventType,
    string EntityType,
    string? EntityId = null,
    string? BeforeJson = null,
    string? AfterJson = null,
    string? Reason = null,
    string? IpAddress = null,
    long? ActorUserId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

/// <summary>Stages meaningful audit events on the caller's unit of work.</summary>
public interface IAuditService
{
    /// <summary>
    /// Stages one audit row. The row is not saved here: callers invoke this
    /// before their own <c>SaveChanges</c>/commit so the audit row and the
    /// change it describes commit or roll back together.
    /// </summary>
    /// <remarks>
    /// Argument validation runs synchronously, so invalid entries throw
    /// directly from this method (it is not <c>async</c>). Staging failures are
    /// logged and rethrown; callers must not swallow them.
    /// </remarks>
    Task WriteAsync(AuditEventEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal audit writer for reference-data changes (M2-02, docs/DATABASE.md §10).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WriteAsync"/> only <em>stages</em> the row on the caller's
/// <see cref="AppDbContext"/>; callers invoke it before their own
/// <c>SaveChanges</c>/commit so the audit row participates in the caller's
/// transaction and commits atomically with the change.
/// </para>
/// <para>
/// If staging fails the service logs the error and lets the exception propagate:
/// the operation fails rather than commit unaudited. A caller transaction that
/// rolls back therefore discards the audit row together with the change it
/// describes.
/// </para>
/// </remarks>
public sealed class AuditService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<AuditService> logger) : IAuditService
{
    public Task WriteAsync(AuditEventEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntityType);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            dbContext.AuditEvents.Add(new AuditEvent
            {
                ActorUserId = entry.ActorUserId,
                EventType = entry.EventType,
                EntityType = entry.EntityType,
                EntityId = entry.EntityId,
                BeforeJson = entry.BeforeJson,
                AfterJson = entry.AfterJson,
                Reason = entry.Reason,
                IpAddress = entry.IpAddress,
                CreatedAt = timeProvider.GetUtcNow(),
                MetadataJson = entry.Metadata is null ? "{}" : JsonSerializer.Serialize(entry.Metadata),
            });
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to stage audit event {EventType} for {EntityType} '{EntityId}'.",
                entry.EventType,
                entry.EntityType,
                entry.EntityId);
            throw;
        }

        return Task.CompletedTask;
    }
}
