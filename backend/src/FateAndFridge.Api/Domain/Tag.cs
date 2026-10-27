namespace FateAndFridge.Api.Domain;

/// <summary>Free-form recipe tag (docs/DATABASE.md §7).</summary>
public sealed class Tag
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Lowercase, trimmed, whitespace-collapsed, diacritics folded; unique.</summary>
    public required string NormalizedName { get; set; }
}
