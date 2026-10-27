namespace FateAndFridge.Api.Domain;

/// <summary>
/// Measurement unit (docs/DATABASE.md §5). <see cref="BaseFactor"/> converts to
/// the dimension base unit (grams for mass, millilitres for volume); it is null
/// for count and nonconvertible units. The dimension is immutable after
/// creation.
/// </summary>
public sealed class Unit
{
    public long Id { get; set; }

    public required string Symbol { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Lowercase, trimmed, whitespace-collapsed, diacritics folded; unique.</summary>
    public required string NormalizedName { get; set; }

    public UnitDimension Dimension { get; set; }

    /// <summary>Conversion factor to the dimension base unit; editable for mass/volume units.</summary>
    public decimal? BaseFactor { get; set; }

    public bool IsCustom { get; set; }

    public bool IsActive { get; set; } = true;
}
