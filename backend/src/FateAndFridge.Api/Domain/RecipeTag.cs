namespace FateAndFridge.Api.Domain;

/// <summary>
/// Many-to-many link between a recipe and its tags (docs/DATABASE.md §7).
/// The pair is the primary key, so duplicate links are impossible.
/// </summary>
public sealed class RecipeTag
{
    public long RecipeId { get; set; }

    public long TagId { get; set; }
}
