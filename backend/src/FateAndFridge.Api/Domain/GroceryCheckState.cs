namespace FateAndFridge.Api.Domain;

/// <summary>
/// Household-shared checkbox state for one grocery line (docs/M8_PLAN.md §1.10,
/// §2.3). The stable <see cref="ItemIdentityHash"/> is canonical ingredient +
/// dimension (or <c>count</c>) for recipe-derived lines, and the manual item id
/// for manual lines, so a check survives quantity and range changes. The unique
/// hash means there is one shared row per line for the whole household.
/// </summary>
public sealed class GroceryCheckState
{
    public long Id { get; set; }

    /// <summary>Stable line identity; unique across the household list.</summary>
    public required string ItemIdentityHash { get; set; }

    public bool Checked { get; set; }

    /// <summary>The member who last checked or unchecked the line, or null.</summary>
    public long? CheckedByUserId { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
