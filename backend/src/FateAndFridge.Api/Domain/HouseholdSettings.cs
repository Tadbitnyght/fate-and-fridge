namespace FateAndFridge.Api.Domain;

/// <summary>
/// Singleton household configuration row. PostgreSQL enforces a single row with
/// <c>id = 1</c>. Timestamps are set by the application and are always UTC.
/// </summary>
public sealed class HouseholdSettings
{
    public long Id { get; set; }

    public required string HouseholdName { get; set; }

    public required string Timezone { get; set; }

    public int DefaultDinerCount { get; set; }

    /// <summary>Household-local day the calendar week starts on: 0 = Sunday … 6 = Saturday.</summary>
    public int WeekStartsOn { get; set; }

    /// <summary>
    /// The last household-local date that was historical under a previous
    /// timezone (docs/M5_PLAN.md §1.7). A date at or before this boundary is
    /// historical even if the timezone later moves, so history can never become
    /// editable again. Null until the timezone first changes.
    /// </summary>
    public DateOnly? HistoricalBoundaryDate { get; set; }

    /// <summary>
    /// Randomizer scoring weights and search windows (docs/M6_PLAN.md §1.3).
    /// Defaults are provided by the database so existing and freshly set-up
    /// rows start on the documented values.
    /// </summary>
    public int RandomizerRepeatLookbackDays { get; set; }

    public int RandomizerRepeatLookaheadDays { get; set; }

    public decimal RandomizerRepeatMultiplier { get; set; }

    public decimal RandomizerSpecialDayMultiplier { get; set; }

    public decimal RandomizerProteinMultiplier { get; set; }

    public int RandomizerProteinNeighborDistance { get; set; }

    public decimal RandomizerFavoriteMultiplier { get; set; }

    public int RandomizerSpacingNearDays { get; set; }

    public int RandomizerSpacingFarDays { get; set; }

    public int RandomizerSpacingSearchDays { get; set; }

    public StartupPage StartupPage { get; set; }

    public AuthMode AuthMode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
