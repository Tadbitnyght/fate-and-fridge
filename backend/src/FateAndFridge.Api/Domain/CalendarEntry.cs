namespace FateAndFridge.Api.Domain;

/// <summary>
/// One planned meal or special event for a household-local date
/// (docs/DATABASE.md §8). There is at most one row per <see cref="MealDate"/>.
/// Type-specific fields are nullable; blank dates have no row at all.
/// </summary>
/// <remarks>
/// <see cref="SnapshotRecipeName"/> and <see cref="SnapshotDisplayText"/>
/// preserve the historical display meaning if the referenced recipe is later
/// renamed, retired, or deleted. Non-historical entries refresh the snapshot
/// when the recipe is renamed; historical entries are frozen.
/// </remarks>
public sealed class CalendarEntry
{
    public long Id { get; set; }

    /// <summary>Household-local calendar date; unique.</summary>
    public DateOnly MealDate { get; set; }

    public CalendarEntryType EntryType { get; set; }

    public long? RecipeId { get; set; }

    public bool IsLocked { get; set; }

    /// <summary>Stored override; <c>null</c> means use the household default.</summary>
    public int? DinerCount { get; set; }

    public string? RestaurantName { get; set; }

    /// <summary>Go To Family Dinner host/location.</summary>
    public string? LocationText { get; set; }

    public string? CustomLabel { get; set; }

    public string? Notes { get; set; }

    /// <summary>Curated icon key, or null.</summary>
    public string? IconKey { get; set; }

    /// <summary>Derived on write from the entry type and any attached recipe.</summary>
    public bool IncludeGroceries { get; set; }

    public string? SnapshotRecipeName { get; set; }

    public required string SnapshotDisplayText { get; set; }

    public long? CreatedByUserId { get; set; }

    public long? UpdatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Recipe? Recipe { get; set; }
}
