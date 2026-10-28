namespace FateAndFridge.Api.Domain;

/// <summary>
/// A persisted batch-reroll preview. A preview
/// generates the proposal and stores it server-side under an opaque
/// <see cref="Token"/> with a 15-minute TTL; Apply consumes the draft. The
/// proposal is never applied implicitly and preview persists no calendar state.
/// </summary>
public sealed class CalendarRerollDraft
{
    public long Id { get; set; }

    /// <summary>Opaque token returned to the client; unique.</summary>
    public Guid Token { get; set; }

    /// <summary>One of <c>seven_days</c>, <c>rest_of_month</c>, <c>fill_empty</c>.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Inclusive household-local range start.</summary>
    public DateOnly StartDate { get; set; }

    /// <summary>Inclusive household-local range end.</summary>
    public DateOnly EndDate { get; set; }

    /// <summary>Creator, or null for the system actor.</summary>
    public long? ActorUserId { get; set; }

    /// <summary>Per-date proposal: action, reason, chosen recipe, score breakdown, before-state.</summary>
    public string ProposalJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><see cref="CreatedAt"/> + the 15-minute preview TTL.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when Apply consumes the draft; null while still pending.</summary>
    public DateTimeOffset? AppliedAt { get; set; }
}
