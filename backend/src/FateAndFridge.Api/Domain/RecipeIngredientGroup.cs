namespace FateAndFridge.Api.Domain;

/// <summary>
/// Optional ordered ingredient group within a recipe (docs/DATABASE.md §6).
/// Titles are optional and ungrouped ingredients are allowed.
/// </summary>
public sealed class RecipeIngredientGroup
{
    public long Id { get; set; }

    public long RecipeId { get; set; }

    public string? Title { get; set; }

    public int SortOrder { get; set; }
}
