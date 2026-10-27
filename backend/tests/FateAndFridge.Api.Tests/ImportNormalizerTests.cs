using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Verification of folded unit resolution and quantity clamping for imported
/// ingredients (docs/M4_PLAN.md §1.11–§1.12). All logic is deterministic under
/// invariant globalization: folding is table-driven through
/// <c>ReferenceDataNormalizer</c> and never <c>String.Normalize</c>.
/// </summary>
public sealed class ImportNormalizerTests
{
    [Fact]
    public void Resolves_symbols_and_aliases_case_insensitively()
    {
        var normalizer = Standard();

        var bySymbol = normalizer.ResolveUnit("cups");
        bySymbol.MatchedUnitId.Should().Be(3);
        bySymbol.MatchedSymbol.Should().Be("cup");
        bySymbol.IsUnknown.Should().BeFalse();
        bySymbol.SuggestedDimension.Should().BeNull();

        normalizer.ResolveUnit("tablespoons").MatchedUnitId.Should().Be(2);
        normalizer.ResolveUnit("Tbsp").MatchedUnitId.Should().Be(2);
        normalizer.ResolveUnit("CUP").MatchedUnitId.Should().Be(3);
    }

    [Fact]
    public void Resolves_display_names()
    {
        var normalizer = Standard();

        normalizer.ResolveUnit("fluid ounce").MatchedUnitId.Should().Be(4);
        normalizer.ResolveUnit("pounds").MatchedUnitId.Should().Be(5);
    }

    [Fact]
    public void Folds_periods_and_whitespace_for_matching()
    {
        var normalizer = Standard();

        normalizer.ResolveUnit("Tbsp.").MatchedUnitId.Should().Be(2);
        var flOz = normalizer.ResolveUnit("FL.  OZ.");
        flOz.MatchedUnitId.Should().Be(4);
        flOz.MatchedSymbol.Should().Be("fl oz");
    }

    [Fact]
    public void Matched_unit_carries_itself_as_the_single_suggestion()
    {
        var unit = Standard().ResolveUnit("cups");

        unit.Suggestions.Should().ContainSingle();
        unit.Suggestions[0].Id.Should().Be(3);
        unit.Suggestions[0].Symbol.Should().Be("cup");
        unit.Suggestions[0].DisplayName.Should().Be("cup");
    }

    [Theory]
    [InlineData("ml", "volume")]
    [InlineData("milliliters", "volume")]
    [InlineData("mg", "mass")]
    [InlineData("dozen", "count")]
    public void Unknown_unit_reports_an_unambiguous_dimension(string rawUnit, string expected)
    {
        var unit = Standard().ResolveUnit(rawUnit);

        unit.Raw.Should().Be(rawUnit);
        unit.MatchedUnitId.Should().BeNull();
        unit.MatchedSymbol.Should().BeNull();
        unit.IsUnknown.Should().BeTrue();
        unit.SuggestedDimension.Should().Be(expected);
    }

    [Fact]
    public void Unknown_unit_without_a_hint_has_no_dimension()
    {
        var unit = Standard().ResolveUnit("smidgen");

        unit.IsUnknown.Should().BeTrue();
        unit.MatchedUnitId.Should().BeNull();
        unit.SuggestedDimension.Should().BeNull();
    }

    [Theory]
    [InlineData("grams")]
    [InlineData("kg")]
    [InlineData("kilograms")]
    public void Mass_hint_covers_gram_and_kilogram(string rawUnit)
    {
        var unit = Standard().ResolveUnit(rawUnit);

        unit.IsUnknown.Should().BeTrue();
        unit.SuggestedDimension.Should().Be("mass");
    }

    [Fact]
    public void Case_sensitive_exact_match_distinguishes_T_from_t()
    {
        var normalizer = Standard();

        normalizer.ResolveUnit("T").MatchedUnitId.Should().Be(2, "'T' is the tablespoon abbreviation");
        normalizer.ResolveUnit("T").MatchedSymbol.Should().Be("tbsp");
        normalizer.ResolveUnit("t").MatchedUnitId.Should().Be(1, "'t' is the teaspoon abbreviation");
        normalizer.ResolveUnit("t").MatchedSymbol.Should().Be("tsp");
    }

    [Fact]
    public void Parser_and_normalizer_resolve_T_and_t_end_to_end()
    {
        var normalizer = Standard();

        var bigT = normalizer.ResolveUnit(normalizer.Parse("1 T butter").UnitText);
        bigT.MatchedUnitId.Should().Be(2);

        var littleT = normalizer.ResolveUnit(normalizer.Parse("1 t butter").UnitText);
        littleT.MatchedUnitId.Should().Be(1);
    }

    [Fact]
    public void Ambiguous_folded_key_is_unknown_with_suggestions()
    {
        ImportUnitCandidate[] units =
        [
            new(1, "zz", "zz", UnitDimension.Count, []),
            new(2, "w", "w", UnitDimension.Count, ["ZZ"]),
        ];

        var unit = new ImportNormalizer(units).ResolveUnit("zZ");

        unit.Raw.Should().Be("zZ");
        unit.MatchedUnitId.Should().BeNull("the folded key 'zz' names two distinct units");
        unit.IsUnknown.Should().BeTrue();
        unit.Suggestions.Should().HaveCount(2);
        unit.Suggestions.Select(suggestion => suggestion.Id).Should().Equal(2, 1);
    }

    [Fact]
    public void Null_or_blank_unit_is_unknown_with_no_raw_text()
    {
        var normalizer = Standard();

        foreach (var raw in new[] { null, string.Empty, "   " })
        {
            var unit = normalizer.ResolveUnit(raw);
            unit.Raw.Should().BeNull();
            unit.IsUnknown.Should().BeTrue();
            unit.MatchedUnitId.Should().BeNull();
            unit.Suggestions.Should().BeEmpty();
            unit.SuggestedDimension.Should().BeNull();
        }
    }

    [Fact]
    public void Suggestions_are_bounded_to_five()
    {
        var units = Enumerable.Range(1, 7)
            .Select(index => new ImportUnitCandidate(
                index,
                $"zz{index}",
                $"zz{index}",
                UnitDimension.Count,
                [$"zz{index}s"]))
            .ToArray();

        var unit = new ImportNormalizer(units).ResolveUnit("zz");

        unit.IsUnknown.Should().BeTrue();
        unit.Suggestions.Should().HaveCount(ImportNormalizer.MaxSuggestions);
    }

    [Fact]
    public void Near_match_suggestions_prefer_prefix_matches()
    {
        var normalizer = Standard();

        var unit = normalizer.ResolveUnit("cu");

        unit.IsUnknown.Should().BeTrue();
        unit.Suggestions.Should().Contain(suggestion => suggestion.Symbol == "cup");
    }

    [Fact]
    public void Clamp_quantity_bounds_and_rounds_to_fourteen_places()
    {
        ImportNormalizer.ClampQuantity(null).Should().BeNull();
        ImportNormalizer.ClampQuantity(-3m).Should().Be(0m);
        ImportNormalizer.ClampQuantity(2_000_000m).Should().Be(1_000_000m);
        ImportNormalizer.ClampQuantity(42.5m).Should().Be(42.5m);

        const decimal longValue = 1.234567890123456789m;
        var clamped = ImportNormalizer.ClampQuantity(longValue);

        clamped.Should().Be(decimal.Round(longValue, 14));
        clamped!.Value.Should().Be(decimal.Round(clamped.Value, 14));
    }

    [Fact]
    public void Fold_is_invariant_globalization_safe()
    {
        ImportUnitText.Fold("Tbsp.").Should().Be("tbsp");
        ImportUnitText.Fold("  FL.  OZ.  ").Should().Be("fl oz");
        ImportUnitText.Fold("Caf\u00E9").Should().Be("cafe");
        ImportUnitText.Fold(null).Should().BeEmpty();
    }

    [Fact]
    public void Normalizer_parses_lines_using_its_own_catalog()
    {
        var parsed = Standard().Parse("1 1/2 cups all-purpose flour, sifted");

        parsed.Quantity.Should().Be(1.5m);
        parsed.UnitText.Should().Be("cups");
        parsed.Name.Should().Be("all-purpose flour");
        parsed.PreparationNote.Should().Be("sifted");
    }

    [Fact]
    public void Brace_characters_in_an_ingredient_name_are_preserved()
    {
        // BUG-008: import normalization folds units and whitespace only; a literal
        // brace in the ingredient name must survive verbatim.
        var parsed = Standard().Parse("1 onion {owder, diced");

        parsed.Name.Should().Be("onion {owder");
        parsed.PreparationNote.Should().Be("diced");
    }


    private static ImportNormalizer Standard() => new(
    [
        new ImportUnitCandidate(1, "tsp", "teaspoon", UnitDimension.Volume, ["teaspoon", "teaspoons", "t"]),
        new ImportUnitCandidate(2, "tbsp", "tablespoon", UnitDimension.Volume, ["tablespoon", "tablespoons", "Tbsp", "T"]),
        new ImportUnitCandidate(3, "cup", "cup", UnitDimension.Volume, ["cups"]),
        new ImportUnitCandidate(4, "fl oz", "fluid ounce", UnitDimension.Volume, ["floz", "fl. oz.", "fluid ounce", "fluid ounces"]),
        new ImportUnitCandidate(5, "lb", "pound", UnitDimension.Mass, ["pound", "pounds", "lbs"]),
        new ImportUnitCandidate(14, "can", "can", UnitDimension.Count, ["cans"]),
    ]);
}
