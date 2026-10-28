namespace FateAndFridge.Api.Domain;

/// <summary>
/// Alternate spelling for a unit. Uniqueness is per unit, not global: the
/// authoritative seed contains both "t" (teaspoon) and "T" (tablespoon), which
/// normalize to the same alias text.
/// </summary>
public sealed class UnitAlias
{
    public long Id { get; set; }

    public long UnitId { get; set; }

    public required string Alias { get; set; }

    /// <summary>Normalized alias text; unique within the owning unit.</summary>
    public required string NormalizedAlias { get; set; }
}
