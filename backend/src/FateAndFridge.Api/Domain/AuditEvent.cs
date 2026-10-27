namespace FateAndFridge.Api.Domain;

/// <summary>
/// Stable security/administration record of a meaningful change
/// (docs/DATABASE.md §10). <see cref="EntityId"/> is text so future entity key
/// types fit; <see cref="MetadataJson"/> defaults to an empty JSON object.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }

    /// <summary>Nullable because system actions have no acting user.</summary>
    public long? ActorUserId { get; set; }

    public required string EventType { get; set; }

    public required string EntityType { get; set; }

    public string? EntityId { get; set; }

    /// <summary>JSONB snapshot before the change, or null when not applicable.</summary>
    public string? BeforeJson { get; set; }

    /// <summary>JSONB snapshot after the change, or null when not applicable.</summary>
    public string? AfterJson { get; set; }

    public string? Reason { get; set; }

    public string? IpAddress { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>JSONB event-specific details; defaults to an empty object.</summary>
    public string MetadataJson { get; set; } = "{}";
}
