namespace FateAndFridge.Api.Domain;

/// <summary>
/// A household recipe (docs/DATABASE.md §3). <see cref="NormalizedName"/> is the
/// unique lowercase lookup form; timestamps are set by the application and are
/// always UTC. Recipes are retired rather than hard-deleted through the ordinary
/// UI.
/// </summary>
public sealed class Recipe
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Lowercase, trimmed, whitespace-collapsed, diacritics folded; unique.</summary>
    public required string NormalizedName { get; set; }

    public string? Description { get; set; }

    public string? SourceSite { get; set; }

    public string? SourceUrl { get; set; }

    /// <summary>Lowercase, trimmed lookup form of <see cref="SourceUrl"/>; not unique.</summary>
    public string? NormalizedSourceUrl { get; set; }

    public string? Author { get; set; }

    public int? PrepMinutes { get; set; }

    public int? CookMinutes { get; set; }

    public string? Notes { get; set; }

    public bool IsFavorite { get; set; }

    public RecipeStatus Status { get; set; }

    public bool IsVegan { get; set; }

    public bool IsVegetarian { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
