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

    public StartupPage StartupPage { get; set; }

    public AuthMode AuthMode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
