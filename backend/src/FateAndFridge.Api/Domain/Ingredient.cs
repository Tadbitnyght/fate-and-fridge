namespace FateAndFridge.Api.Domain;

/// <summary>
/// Canonical ingredient master record (docs/DATABASE.md §4). A merged source
/// ingredient keeps its row, points at the target through
/// <see cref="MergedIntoIngredientId"/>, and is retired rather than deleted.
/// </summary>
public sealed class Ingredient
{
    public long Id { get; set; }

    public required string CanonicalName { get; set; }

    /// <summary>Lowercase, trimmed, whitespace-collapsed, diacritics folded; unique.</summary>
    public required string NormalizedName { get; set; }

    public long? DepartmentId { get; set; }

    /// <summary>Merge target; set on the retired source ingredient for traceability.</summary>
    public long? MergedIntoIngredientId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
