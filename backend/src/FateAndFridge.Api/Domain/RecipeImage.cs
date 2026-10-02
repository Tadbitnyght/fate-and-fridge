namespace FateAndFridge.Api.Domain;

/// <summary>
/// The single active local image for a recipe (docs/DATABASE.md §3). Image
/// history is not kept; replacement and removal are atomic.
/// </summary>
public sealed class RecipeImage
{
    public long Id { get; set; }

    public long RecipeId { get; set; }

    public required string StoragePath { get; set; }

    public string? SourceUrl { get; set; }

    public string? AltText { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
