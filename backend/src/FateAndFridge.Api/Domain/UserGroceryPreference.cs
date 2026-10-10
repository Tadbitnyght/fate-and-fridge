namespace FateAndFridge.Api.Domain;

/// <summary>
/// Per-user grocery list preferences (docs/M8_PLAN.md §1.7, §1.11, §2.3).
/// <see cref="UserId"/> is the primary key, so a user has at most one row and
/// the "unique user preference" is structural. The last-used range is
/// remembered so the list reopens where the user left it; the first-open
/// default is Next 7 Days. Timestamps are UTC.
/// </summary>
public sealed class UserGroceryPreference
{
    /// <summary>Owner; also the primary key (one row per user).</summary>
    public long UserId { get; set; }

    public bool HideChecked { get; set; }

    /// <summary>One of <c>next_3_days</c>, <c>next_7_days</c>, <c>next_14_days</c>, <c>rest_of_month</c>, <c>custom</c>.</summary>
    public string? RangePreset { get; set; }

    public DateOnly? RangeStart { get; set; }

    public int? RangeDays { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
