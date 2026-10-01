namespace FateAndFridge.Api.Domain;

/// <summary>
/// Primary protein used by the randomizer's neighbouring-protein heuristic
/// (docs/RANDOMIZER.md, docs/DATABASE.md §7).
/// </summary>
public sealed class Protein
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Lowercase, trimmed, whitespace-collapsed, diacritics folded; unique.</summary>
    public required string NormalizedName { get; set; }

    public bool IsActive { get; set; } = true;
}
