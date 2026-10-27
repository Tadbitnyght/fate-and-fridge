namespace FateAndFridge.Api.Domain;

/// <summary>
/// Grocery store department used to group and order the grocery list
/// (docs/DATABASE.md §4). <see cref="NormalizedName"/> is the unique
/// lowercase lookup form; timestamps are set by the application and are
/// always UTC.
/// </summary>
public sealed class GroceryDepartment
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Lowercase, trimmed, whitespace-collapsed, diacritics folded; unique.</summary>
    public required string NormalizedName { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
