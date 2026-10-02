namespace FateAndFridge.Api.Domain;

/// <summary>
/// One ordered instruction step inside a section (docs/DATABASE.md §3).
/// </summary>
public sealed class RecipeInstructionStep
{
    public long Id { get; set; }

    public long SectionId { get; set; }

    public required string Text { get; set; }

    public int SortOrder { get; set; }
}
