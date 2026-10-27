namespace FateAndFridge.Api.Domain;

/// <summary>
/// Alternate name for an ingredient. <see cref="NormalizedAlias"/> is globally
/// unique so an alias resolves to exactly one canonical ingredient
/// (docs/DATABASE.md §4).
/// </summary>
public sealed class IngredientAlias
{
    public long Id { get; set; }

    public long IngredientId { get; set; }

    public required string Alias { get; set; }

    /// <summary>Lowercase, trimmed, whitespace-collapsed, diacritics folded; globally unique.</summary>
    public required string NormalizedAlias { get; set; }
}
