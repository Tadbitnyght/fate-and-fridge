using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Exhaustive matrix for the M2-07 pure unit conversion service
/// (docs/M2_PLAN.md §2 seed table, docs/DATABASE.md §5 factors,
/// docs/ARCHITECTURE.md §15 families): exact decimal math for the seeded and
/// overridden factors, same-unit count identity, nonconvertible and
/// null-quantity rules, and every <see cref="ConversionFailureReason"/>.
/// Decimal assertions use exact <c>==</c>; no tolerance is needed or allowed.
/// </summary>
public sealed class UnitConversionServiceTests
{
    private readonly UnitConversionService _service = new();

    private static readonly IReadOnlyDictionary<string, ConversionUnit> SeedUnits =
        ReferenceDataSeedCatalog.Units.ToDictionary(
            unit => unit.Symbol,
            unit => new ConversionUnit(null, unit.Symbol, unit.Dimension, unit.BaseFactor),
            StringComparer.Ordinal);

    // ---- Seeded default table (docs/M2_PLAN.md §2) ---------------------------

    public static TheoryData<string, decimal> BaseFactorCases { get; } = new()
    {
        { "tsp", 4.92892159375m },
        { "tbsp", 14.78676478125m },
        { "fl oz", 29.5735295625m },
        { "cup", 236.5882365m },
        { "pint", 473.176473m },
        { "quart", 946.352946m },
        { "gallon", 3785.411784m },
        { "oz", 28.349523125m },
        { "lb", 453.59237m },
        { "g", 1m },
        { "kg", 1000m },
    };

    public static TheoryData<string, decimal, string, decimal> ExactConversions { get; } = new()
    {
        { "tsp", 3m, "tbsp", 1m },
        { "tbsp", 1m, "tsp", 3m },
        { "tsp", 6m, "fl oz", 1m },
        { "fl oz", 1m, "tsp", 6m },
        { "tbsp", 2m, "fl oz", 1m },
        { "fl oz", 1m, "tbsp", 2m },
        { "tsp", 48m, "cup", 1m },
        { "cup", 1m, "tsp", 48m },
        { "tbsp", 16m, "cup", 1m },
        { "cup", 1m, "tbsp", 16m },
        { "fl oz", 8m, "cup", 1m },
        { "cup", 1m, "fl oz", 8m },
        { "cup", 2m, "pint", 1m },
        { "pint", 1m, "cup", 2m },
        { "fl oz", 16m, "pint", 1m },
        { "pint", 1m, "fl oz", 16m },
        { "pint", 2m, "quart", 1m },
        { "quart", 1m, "pint", 2m },
        { "cup", 4m, "quart", 1m },
        { "quart", 1m, "cup", 4m },
        { "quart", 4m, "gallon", 1m },
        { "gallon", 1m, "quart", 4m },
        { "pint", 8m, "gallon", 1m },
        { "gallon", 1m, "pint", 8m },
        { "cup", 16m, "gallon", 1m },
        { "gallon", 1m, "cup", 16m },
        { "gallon", 1m, "fl oz", 128m },
        { "quart", 1m, "fl oz", 32m },
        { "cup", 1m, "pint", 0.5m },
        { "oz", 16m, "lb", 1m },
        { "lb", 1m, "oz", 16m },
        { "g", 1000m, "kg", 1m },
        { "kg", 1m, "g", 1000m },
        { "g", 28.349523125m, "oz", 1m },
        { "oz", 1m, "g", 28.349523125m },
        { "g", 453.59237m, "lb", 1m },
        { "lb", 1m, "g", 453.59237m },
        { "lb", 1m, "kg", 0.45359237m },
    };

    public static TheoryData<decimal> NonPositiveQuantities { get; } = new()
    {
        0m,
        -1m,
        -0.5m,
        -1e-28m,
    };

    [Fact]
    public void Seed_units_are_the_documented_twenty_one()
    {
        SeedUnits.Should().HaveCount(21);
        SeedUnits.Values.Count(unit => unit.Dimension == UnitDimension.Volume).Should().Be(7);
        SeedUnits.Values.Count(unit => unit.Dimension == UnitDimension.Mass).Should().Be(4);
        SeedUnits.Values.Count(unit => unit.Dimension == UnitDimension.Count).Should().Be(9);
        SeedUnits.Values.Count(unit => unit.Dimension == UnitDimension.Nonconvertible).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(BaseFactorCases))]
    public void ToBase_of_one_unit_is_exactly_the_documented_factor(string symbol, decimal factor) =>
        ToBase(SeedUnits[symbol], 1m).Should().Be(factor);

    [Theory]
    [MemberData(nameof(ExactConversions))]
    public void Seed_units_convert_exactly(string fromSymbol, decimal quantity, string toSymbol, decimal expected) =>
        Converted(SeedUnits[fromSymbol], SeedUnits[toSymbol], quantity).Should().Be(expected);

    [Fact]
    public void Every_seed_unit_has_a_defined_base_or_failure_contract()
    {
        foreach (var unit in SeedUnits.Values)
        {
            var result = _service.ToBase(unit, 1m);

            switch (unit.Dimension)
            {
                case UnitDimension.Mass:
                case UnitDimension.Volume:
                    result.Should().Be(new ConversionResult.Success(unit.BaseFactor!.Value), unit.Symbol);
                    break;
                case UnitDimension.Count:
                    result.Should().Be(new ConversionResult.Success(1m), unit.Symbol);
                    break;
                case UnitDimension.Nonconvertible:
                    result.Should().Be(
                        new ConversionResult.Failure(ConversionFailureReason.NonConvertible), unit.Symbol);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled dimension {unit.Dimension}.");
            }
        }
    }

    [Fact]
    public void Converting_a_mass_or_volume_unit_to_itself_is_identity_for_every_seed_unit()
    {
        foreach (var unit in SeedUnits.Values.Where(
            candidate => candidate.Dimension is UnitDimension.Mass or UnitDimension.Volume))
        {
            Converted(unit, unit, 7.5m).Should().Be(7.5m, unit.Symbol);
        }
    }

    [Fact]
    public void Decimal_division_rounds_only_when_the_quotient_does_not_terminate()
    {
        // 1 tsp = 1/48 cup exactly; the service applies the documented decimal
        // division semantics and performs no additional rounding or truncation.
        Converted(SeedUnits["tsp"], SeedUnits["cup"], 1m).Should().Be(1m / 48m);
    }

    // ---- Overridden factors --------------------------------------------------

    [Fact]
    public void Overridden_factors_flow_through_conversion_and_base_normalization()
    {
        var millilitre = new ConversionUnit(9001, "mL", UnitDimension.Volume, 1m);
        var overriddenCup = new ConversionUnit(42, "cup", UnitDimension.Volume, 240m);

        ToBase(overriddenCup, 1m).Should().Be(240m);
        Converted(overriddenCup, millilitre, 1.5m).Should().Be(360m);
        Converted(millilitre, overriddenCup, 480m).Should().Be(2m);
    }

    [Fact]
    public void Overriding_the_cup_factor_changes_cup_conversions_but_leaves_other_units_alone()
    {
        var seededCup = SeedUnits["cup"];
        var overriddenCup = seededCup with { BaseFactor = 240m };

        Converted(seededCup, SeedUnits["tsp"], 1m).Should().Be(48m);
        Converted(overriddenCup, SeedUnits["tsp"], 1m).Should().Be(240m / 4.92892159375m);
    }

    // ---- Count units ---------------------------------------------------------

    [Fact]
    public void Same_count_unit_by_id_converts_as_identity()
    {
        var can = new ConversionUnit(7, "can", UnitDimension.Count, null);
        var sameIdDifferentSymbol = new ConversionUnit(7, "tin", UnitDimension.Count, null);

        Converted(can, sameIdDifferentSymbol, 3m).Should().Be(3m);
        _service.CanCombine(can, sameIdDifferentSymbol).Should().BeTrue();
    }

    [Fact]
    public void Same_count_unit_by_symbol_is_case_insensitive_when_ids_are_absent()
    {
        var upper = new ConversionUnit(null, "Can", UnitDimension.Count, null);
        var lower = new ConversionUnit(null, "can", UnitDimension.Count, null);

        Converted(upper, lower, 2.5m).Should().Be(2.5m);
        _service.CanCombine(upper, lower).Should().BeTrue();
    }

    [Fact]
    public void Same_count_symbol_with_one_persisted_id_still_matches_by_symbol()
    {
        var persisted = new ConversionUnit(3, "bunch", UnitDimension.Count, null);
        var unpersisted = new ConversionUnit(null, "Bunch", UnitDimension.Count, null);

        Converted(persisted, unpersisted, 4m).Should().Be(4m);
        _service.CanCombine(persisted, unpersisted).Should().BeTrue();
    }

    [Fact]
    public void Different_count_ids_do_not_match_even_with_equal_symbols()
    {
        var firstCan = new ConversionUnit(1, "can", UnitDimension.Count, null);
        var secondCan = new ConversionUnit(2, "can", UnitDimension.Count, null);

        AssertConvertFailure(firstCan, secondCan, 2m, ConversionFailureReason.DifferentCountUnits);
        _service.CanCombine(firstCan, secondCan).Should().BeFalse();
    }

    [Theory]
    [InlineData("can", "jar")]
    [InlineData("package", "can")]
    [InlineData("slice", "pinch")]
    public void Different_count_units_never_convert(string fromSymbol, string toSymbol) =>
        AssertConvertFailure(SeedUnits[fromSymbol], SeedUnits[toSymbol], 1m, ConversionFailureReason.DifferentCountUnits);

    [Fact]
    public void Count_units_ignore_base_factors()
    {
        var oddCount = new ConversionUnit(null, "each", UnitDimension.Count, 5m);

        Converted(oddCount, SeedUnits["each"], 3m).Should().Be(3m);
        ToBase(oddCount, 3m).Should().Be(3m);
    }

    [Fact]
    public void Count_identity_never_overflows()
    {
        Converted(SeedUnits["each"], SeedUnits["each"], decimal.MaxValue).Should().Be(decimal.MaxValue);
        ToBase(SeedUnits["each"], decimal.MaxValue).Should().Be(decimal.MaxValue);
    }

    // ---- Nonconvertible and quantity-less units ------------------------------

    [Fact]
    public void Nonconvertible_units_fail_every_conversion()
    {
        var toTaste = SeedUnits["to taste"];
        var persistedToTaste = new ConversionUnit(99, "to taste", UnitDimension.Nonconvertible, null);

        AssertConvertFailure(toTaste, SeedUnits["g"], 1m, ConversionFailureReason.NonConvertible);
        AssertConvertFailure(SeedUnits["g"], toTaste, 1m, ConversionFailureReason.NonConvertible);
        AssertConvertFailure(toTaste, persistedToTaste, 1m, ConversionFailureReason.NonConvertible);
        AssertConvertFailure(toTaste, SeedUnits["each"], 1m, ConversionFailureReason.NonConvertible);
        AssertToBaseFailure(toTaste, 1m, ConversionFailureReason.NonConvertible);
    }

    [Fact]
    public void Quantity_less_nonconvertible_items_are_out_of_scope_so_m8_carries_them_without_math()
    {
        // docs/DATABASE.md §6 allows a null quantity for "to taste". This service
        // accepts only positive decimals, so every conversion of a nonconvertible
        // unit fails with NonConvertible; M8 grocery aggregation passes such items
        // through untouched instead of calling Convert/ToBase.
        AssertToBaseFailure(SeedUnits["to taste"], 1m, ConversionFailureReason.NonConvertible);
        AssertConvertFailure(SeedUnits["to taste"], SeedUnits["tsp"], 1m, ConversionFailureReason.NonConvertible);
    }

    // ---- Quantity validation -------------------------------------------------

    [Theory]
    [MemberData(nameof(NonPositiveQuantities))]
    public void Non_positive_quantities_fail_with_ZeroOrNegativeQuantity(decimal quantity)
    {
        AssertConvertFailure(SeedUnits["g"], SeedUnits["kg"], quantity, ConversionFailureReason.ZeroOrNegativeQuantity);
        AssertConvertFailure(SeedUnits["each"], SeedUnits["each"], quantity, ConversionFailureReason.ZeroOrNegativeQuantity);
        AssertToBaseFailure(SeedUnits["g"], quantity, ConversionFailureReason.ZeroOrNegativeQuantity);
        AssertToBaseFailure(SeedUnits["each"], quantity, ConversionFailureReason.ZeroOrNegativeQuantity);
    }

    [Fact]
    public void Quantity_validation_precedes_every_other_rule()
    {
        AssertConvertFailure(SeedUnits["to taste"], SeedUnits["g"], 0m, ConversionFailureReason.ZeroOrNegativeQuantity);
        AssertConvertFailure(SeedUnits["tsp"], SeedUnits["g"], -1m, ConversionFailureReason.ZeroOrNegativeQuantity);
        AssertToBaseFailure(SeedUnits["to taste"], -1m, ConversionFailureReason.ZeroOrNegativeQuantity);
        AssertConvertFailure(
            new ConversionUnit(1, "scoop", UnitDimension.Mass, null),
            SeedUnits["g"],
            0m,
            ConversionFailureReason.ZeroOrNegativeQuantity);
    }

    [Fact]
    public void The_smallest_positive_decimal_is_convertible()
    {
        ToBase(SeedUnits["g"], 1e-28m).Should().Be(1e-28m);
    }

    // ---- Missing and unusable factors ----------------------------------------

    [Fact]
    public void Mass_and_volume_units_without_a_usable_factor_fail_as_missing()
    {
        var factorlessMass = new ConversionUnit(10, "scoop", UnitDimension.Mass, null);
        var factorlessVolume = new ConversionUnit(11, "dash", UnitDimension.Volume, null);
        var zeroFactor = new ConversionUnit(12, "zero", UnitDimension.Mass, 0m);
        var negativeFactor = new ConversionUnit(13, "negative", UnitDimension.Volume, -2m);

        AssertConvertFailure(factorlessMass, SeedUnits["g"], 1m, ConversionFailureReason.MissingFactor);
        AssertConvertFailure(SeedUnits["g"], factorlessMass, 1m, ConversionFailureReason.MissingFactor);
        AssertConvertFailure(factorlessVolume, SeedUnits["tsp"], 1m, ConversionFailureReason.MissingFactor);
        AssertConvertFailure(SeedUnits["tsp"], factorlessVolume, 1m, ConversionFailureReason.MissingFactor);
        AssertConvertFailure(zeroFactor, SeedUnits["g"], 1m, ConversionFailureReason.MissingFactor);
        AssertConvertFailure(negativeFactor, SeedUnits["tsp"], 1m, ConversionFailureReason.MissingFactor);
        AssertToBaseFailure(factorlessMass, 1m, ConversionFailureReason.MissingFactor);
        AssertToBaseFailure(zeroFactor, 1m, ConversionFailureReason.MissingFactor);
        AssertToBaseFailure(negativeFactor, 1m, ConversionFailureReason.MissingFactor);
    }

    // ---- Overflow ------------------------------------------------------------

    [Fact]
    public void Overflow_is_reported_as_a_failure_and_never_throws()
    {
        var hugeFactorUnit = new ConversionUnit(20, "huge", UnitDimension.Mass, decimal.MaxValue);
        var tinyFactorUnit = new ConversionUnit(21, "tiny", UnitDimension.Mass, 1e-28m);

        AssertToBaseFailure(SeedUnits["kg"], decimal.MaxValue, ConversionFailureReason.Overflow);
        AssertConvertFailure(SeedUnits["kg"], SeedUnits["g"], decimal.MaxValue, ConversionFailureReason.Overflow);
        AssertConvertFailure(hugeFactorUnit, SeedUnits["g"], 2m, ConversionFailureReason.Overflow);
        AssertConvertFailure(SeedUnits["g"], tinyFactorUnit, decimal.MaxValue, ConversionFailureReason.Overflow);
        AssertToBaseFailure(hugeFactorUnit, 2m, ConversionFailureReason.Overflow);
    }

    // ---- Incompatible dimensions ---------------------------------------------

    [Theory]
    [InlineData("tsp", "g")]
    [InlineData("g", "tsp")]
    [InlineData("each", "g")]
    [InlineData("kg", "cup")]
    [InlineData("quart", "each")]
    public void Cross_dimension_conversions_fail_as_incompatible(string fromSymbol, string toSymbol) =>
        AssertConvertFailure(
            SeedUnits[fromSymbol], SeedUnits[toSymbol], 1m, ConversionFailureReason.IncompatibleDimensions);

    // ---- CanCombine ----------------------------------------------------------

    [Fact]
    public void CanCombine_is_true_for_mass_pairs_volume_pairs_and_same_count_units()
    {
        _service.CanCombine(SeedUnits["g"], SeedUnits["lb"]).Should().BeTrue();
        _service.CanCombine(SeedUnits["tsp"], SeedUnits["gallon"]).Should().BeTrue();
        _service.CanCombine(SeedUnits["can"], SeedUnits["can"]).Should().BeTrue();
        _service.CanCombine(
            new ConversionUnit(5, "can", UnitDimension.Count, null),
            new ConversionUnit(5, "anything", UnitDimension.Count, null)).Should().BeTrue();
        _service.CanCombine(
            new ConversionUnit(null, "Package", UnitDimension.Count, null),
            new ConversionUnit(null, "package", UnitDimension.Count, null)).Should().BeTrue();
    }

    [Fact]
    public void CanCombine_is_structural_for_mass_and_volume_even_without_factors()
    {
        var factorlessMass = new ConversionUnit(30, "scoop", UnitDimension.Mass, null);

        _service.CanCombine(factorlessMass, SeedUnits["g"]).Should().BeTrue();
    }

    [Theory]
    [InlineData("can", "jar")]
    [InlineData("tsp", "g")]
    [InlineData("each", "g")]
    [InlineData("to taste", "to taste")]
    [InlineData("to taste", "g")]
    [InlineData("cup", "each")]
    public void CanCombine_is_false_across_different_count_units_dimensions_and_nonconvertible(
        string aSymbol,
        string bSymbol) =>
        _service.CanCombine(SeedUnits[aSymbol], SeedUnits[bSymbol]).Should().BeFalse();

    // ---- Exhaustive seed-pair contract ---------------------------------------

    [Fact]
    public void Every_seed_unit_pair_follows_the_documented_contract()
    {
        foreach (var from in SeedUnits.Values)
        {
            foreach (var to in SeedUnits.Values)
            {
                var label = $"{from.Symbol} -> {to.Symbol}";
                var result = _service.Convert(from, to, 1m);

                if (from.Dimension == UnitDimension.Nonconvertible || to.Dimension == UnitDimension.Nonconvertible)
                {
                    result.Should().Be(
                        new ConversionResult.Failure(ConversionFailureReason.NonConvertible), label);
                }
                else if (from.Dimension != to.Dimension)
                {
                    result.Should().Be(
                        new ConversionResult.Failure(ConversionFailureReason.IncompatibleDimensions), label);
                }
                else if (from.Dimension == UnitDimension.Count)
                {
                    if (from.Symbol == to.Symbol)
                    {
                        result.Should().Be(new ConversionResult.Success(1m), label);
                    }
                    else
                    {
                        result.Should().Be(
                            new ConversionResult.Failure(ConversionFailureReason.DifferentCountUnits), label);
                    }
                }
                else
                {
                    result.Should().BeOfType<ConversionResult.Success>(label);
                }

                var expectedCombine = from.Dimension != to.Dimension
                    ? false
                    : from.Dimension switch
                    {
                        UnitDimension.Mass or UnitDimension.Volume => true,
                        UnitDimension.Count => from.Symbol == to.Symbol,
                        _ => false,
                    };

                _service.CanCombine(from, to).Should().Be(expectedCombine, label);
            }
        }
    }

    // ---- Helpers -------------------------------------------------------------

    private decimal Converted(ConversionUnit from, ConversionUnit to, decimal quantity) =>
        _service.Convert(from, to, quantity)
            .Should().BeOfType<ConversionResult.Success>()
            .Which.Quantity;

    private decimal ToBase(ConversionUnit unit, decimal quantity) =>
        _service.ToBase(unit, quantity)
            .Should().BeOfType<ConversionResult.Success>()
            .Which.Quantity;

    private void AssertConvertFailure(
        ConversionUnit from,
        ConversionUnit to,
        decimal quantity,
        ConversionFailureReason reason) =>
        _service.Convert(from, to, quantity).Should().Be(new ConversionResult.Failure(reason));

    private void AssertToBaseFailure(ConversionUnit unit, decimal quantity, ConversionFailureReason reason) =>
        _service.ToBase(unit, quantity).Should().Be(new ConversionResult.Failure(reason));
}
