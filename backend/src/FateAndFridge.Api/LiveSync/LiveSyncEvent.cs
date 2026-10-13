namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// Stable type names for live-sync events (docs/M9_PLAN.md §1.5/§2.2). The value
/// is the single discriminator on the client's <c>event</c> handler, so adding
/// an event type never requires a new client method.
/// </summary>
public static class LiveSyncEventType
{
    /// <summary>Any calendar change other than a lock toggle.</summary>
    public const string CalendarUpdated = "calendar.updated";

    /// <summary>A lock or unlock of a calendar date, and only that.</summary>
    public const string CalendarLockChanged = "calendar.lock_changed";

    /// <summary>Manual grocery items, staples, add-to-list, and check state.</summary>
    public const string GroceriesUpdated = "groceries.updated";

    /// <summary>Any household-visible recipe write.</summary>
    public const string RecipeUpdated = "recipe.updated";

    /// <summary>Household/security settings and reference-data writes.</summary>
    public const string SettingsUpdated = "settings.updated";

    /// <summary>Sent to a revoked session's connections immediately before they close.</summary>
    public const string SessionRevoked = "session.revoked";
}

/// <summary>
/// The coarse, stable live-sync envelope broadcast over the SignalR <c>event</c>
/// client method (docs/M9_PLAN.md §1.6/§2.2). Clients use <see cref="Type"/> as
/// the discriminator and refetch; the envelope never carries entity bodies or
/// diffs. It is serialized snake_case to match the rest of the internal API
/// (docs/API.md §3).
/// </summary>
public sealed record LiveSyncEvent
{
    /// <summary>Required event type; one of <see cref="LiveSyncEventType"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Optional coarse entity name (for example <c>groceries</c>).</summary>
    public string? Entity { get; init; }

    /// <summary>Optional entity id when a single row is affected.</summary>
    public string? Id { get; init; }

    /// <summary>Optional id correlating the write with its operation/audit row.</summary>
    public string? OperationId { get; init; }

    /// <summary>The acting user, or <c>null</c> for the single-user system actor.</summary>
    public long? ActorUserId { get; init; }

    /// <summary>Always UTC; the moment the event was published.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Creates an envelope stamped with the current UTC time.</summary>
    public static LiveSyncEvent Create(
        string type,
        string? entity = null,
        string? id = null,
        string? operationId = null,
        long? actorUserId = null) => new()
        {
            Type = type,
            Entity = entity,
            Id = id,
            OperationId = operationId,
            ActorUserId = actorUserId,
            Timestamp = DateTimeOffset.UtcNow,
        };
}
