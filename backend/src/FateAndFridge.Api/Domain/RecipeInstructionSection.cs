namespace FateAndFridge.Api.Domain;

/// <summary>
/// Optional ordered instruction section within a recipe (docs/DATABASE.md §3).
/// </summary>
public sealed class RecipeInstructionSection
{
    public long Id { get; set; }

    public long RecipeId { get; set; }

    public string? Title { get; set; }

    public int SortOrder { get; set; }
}
