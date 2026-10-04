using System.Globalization;

using FateAndFridge.Api.Imports;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Table-driven verification of the pure ingredient-line splitter
/// (docs/M4_PLAN.md §1.12): every accepted quantity form, raw preservation for
/// ranges/multipliers/word quantities, unit-phrase matching, comma preparation
/// notes, parenthetical package sizes, and total behavior on empty/unicode
/// input. No database or network is used.
/// </summary>
public sealed class IngredientLineParserTests
{
    private static readonly string[] KnownUnits =
    [
        "cup", "cups", "tsp", "tbsp", "can", "clove", "cloves", "g", "oz", "lb",
    ];

    /// <summary>Inputs whose parsed quantity (or null) is asserted exactly.</summary>
    public static TheoryData<string, decimal?> QuantityCases => new()
    {
        { "2 cups flour", 2m },
        { "1.5 cups flour", 1.5m },
        { "0.5 cup milk", 0.5m },
        { "1/2 cup milk", 0.5m },
        { "3/4 cup milk", 0.75m },
        { "1 1/2 cups flour", 1.5m },
        { "2 3/4 cups flour", 2.75m },
        { "1\u00BD cups flour", 1.5m },
        { "2 \u00BE cups flour", 2.75m },
        { "\u00BD tsp salt", 0.5m },
        { "\u00BC tsp salt", 0.25m },
        { "100 g flour", 100m },
        { "1 (14 oz) can diced tomatoes", 1m },
        { "3 cloves garlic", 3m },
        { "1 egg", 1m },
        { "3", 3m },
        { "1-2 cups flour", null },
        { "1 to 2 cups flour", null },
        { "1\u20132 cups flour", null },
        { "2x 400g cans tomatoes", null },
        { "some salt", null },
        { "a pinch of salt", null },
        { "salt to taste", null },
        { string.Empty, null },
        { "   ", null },
    };

    [Theory]
    [MemberData(nameof(QuantityCases))]
    public void Parses_quantity_or_preserves_null(string input, decimal? expected)
    {
        var parsed = IngredientLineParser.Parse(input, KnownUnits);

        parsed.Quantity.Should().Be(expected, "input '{0}' must yield the documented quantity", input);
    }

    [Fact]
    public void Parses_every_vulgar_fraction_form()
    {
        (string Input, decimal Expected)[] cases =
        [
            ("\u00BD cup sugar", 0.5m),
            ("\u2153 cup sugar", 1m / 3m),
            ("\u2154 cup sugar", 2m / 3m),
            ("\u00BC cup sugar", 0.25m),
            ("\u00BE cup sugar", 0.75m),
            ("\u2155 cup sugar", 0.2m),
            ("\u2156 cup sugar", 0.4m),
            ("\u2157 cup sugar", 0.6m),
            ("\u2158 cup sugar", 0.8m),
            ("\u2159 cup sugar", 1m / 6m),
            ("\u215A cup sugar", 5m / 6m),
            ("\u2150 cup sugar", 1m / 7m),
            ("\u215B cup sugar", 0.125m),
            ("\u215C cup sugar", 0.375m),
            ("\u215D cup sugar", 0.625m),
            ("\u215E cup sugar", 0.875m),
            ("\u2151 cup sugar", 1m / 9m),
            ("\u2152 cup sugar", 0.1m),
        ];

        foreach (var (input, expected) in cases)
        {
            var parsed = IngredientLineParser.Parse(input, KnownUnits);
            parsed.Quantity.Should().Be(expected, "input '{0}'", input);
            parsed.Name.Should().Be("sugar", "input '{0}'", input);
        }
    }

    [Fact]
    public void Splits_quantity_unit_name_and_preparation_note()
    {
        const string input = "1 1/2 cups all-purpose flour, sifted";

        var parsed = IngredientLineParser.Parse(input, KnownUnits);

        parsed.Raw.Should().Be(input);
        parsed.Quantity.Should().Be(1.5m);
        parsed.UnitText.Should().Be("cups");
        parsed.Name.Should().Be("all-purpose flour");
        parsed.PreparationNote.Should().Be("sifted");
    }

    [Fact]
    public void Keeps_multi_word_preparation_note_intact()
    {
        var parsed = IngredientLineParser.Parse("2 cups flour, sifted, divided", KnownUnits);

        parsed.PreparationNote.Should().Be("sifted, divided");
        parsed.Name.Should().Be("flour");
    }

    [Fact]
    public void Preserves_parenthetical_package_size_before_the_name()
    {
        var parsed = IngredientLineParser.Parse("1 (14 oz) can diced tomatoes", KnownUnits);

        parsed.Quantity.Should().Be(1m);
        parsed.UnitText.Should().Be("can");
        parsed.Name.Should().Be("(14 oz) diced tomatoes");
        parsed.PreparationNote.Should().BeNull();
    }

    [Fact]
    public void Preserves_parenthetical_package_size_with_trailing_note()
    {
        var parsed = IngredientLineParser.Parse("1 (14 oz) can diced tomatoes, drained", KnownUnits);

        parsed.Quantity.Should().Be(1m);
        parsed.UnitText.Should().Be("can");
        parsed.Name.Should().Be("(14 oz) diced tomatoes");
        parsed.PreparationNote.Should().Be("drained");
    }

    [Fact]
    public void Parentheses_after_a_unit_stay_in_the_name()
    {
        var parsed = IngredientLineParser.Parse("1 can (14 oz, drained) tomatoes", KnownUnits);

        parsed.PreparationNote.Should().BeNull("the comma is inside parentheses");
        parsed.UnitText.Should().Be("can");
        parsed.Name.Should().Be("(14 oz, drained) tomatoes");
    }

    [Fact]
    public void Parenthetical_is_kept_when_no_known_unit_matches()
    {
        var parsed = IngredientLineParser.Parse("1 (14 oz) diced tomatoes", KnownUnits);

        parsed.Quantity.Should().Be(1m);
        parsed.UnitText.Should().BeNull();
        parsed.Name.Should().Be("(14 oz) diced tomatoes");
    }

    [Fact]
    public void Prefers_the_longest_known_unit_phrase()
    {
        string[] phrases = ["fl oz", "oz"];

        var parsed = IngredientLineParser.Parse("2 fl oz milk", phrases);

        parsed.UnitText.Should().Be("fl oz");
        parsed.Name.Should().Be("milk");
    }

    [Fact]
    public void Matches_units_ignoring_trailing_period()
    {
        var parsed = IngredientLineParser.Parse("2 Tbsp. oil", ["tbsp"]);

        parsed.Quantity.Should().Be(2m);
        parsed.UnitText.Should().Be("Tbsp.");
        parsed.Name.Should().Be("oil");
    }

    [Fact]
    public void Splits_a_dimension_hint_as_an_unknown_unit()
    {
        var parsed = IngredientLineParser.Parse("250 ml milk", KnownUnits);

        parsed.Quantity.Should().Be(250m);
        parsed.UnitText.Should().Be("ml");
        parsed.Name.Should().Be("milk");
    }

    [Fact]
    public void Leaves_descriptors_attached_to_the_name()
    {
        var parsed = IngredientLineParser.Parse("2 large eggs", KnownUnits);

        parsed.Quantity.Should().Be(2m);
        parsed.UnitText.Should().BeNull();
        parsed.Name.Should().Be("large eggs");
    }

    [Theory]
    [InlineData("1-2 cups flour")]
    [InlineData("1 to 2 cups flour")]
    [InlineData("1 to2 cups flour")]
    [InlineData("1\u20132 cups flour")]
    [InlineData("1\u22122 cups flour")]
    [InlineData("1\u20102 cups flour")]
    [InlineData("1\u20112 cups flour")]
    [InlineData("1\u20122 cups flour")]
    [InlineData("1\u20152 cups flour")]
    [InlineData("1\uFF0D2 cups flour")]
    public void Preserves_ranges_verbatim(string input)
    {
        var parsed = IngredientLineParser.Parse(input, KnownUnits);

        parsed.Raw.Should().Be(input);
        parsed.Quantity.Should().BeNull();
        parsed.UnitText.Should().BeNull();
        parsed.Name.Should().Be(input);
        parsed.PreparationNote.Should().BeNull();
    }

    [Theory]
    [InlineData("2x 400g cans tomatoes")]
    [InlineData("2x4 cans tomatoes")]
    public void Preserves_multipliers_verbatim(string input)
    {
        var parsed = IngredientLineParser.Parse(input, KnownUnits);

        parsed.Quantity.Should().BeNull();
        parsed.UnitText.Should().BeNull();
        parsed.Name.Should().Be(input);
    }

    [Theory]
    [InlineData("a pinch of salt")]
    [InlineData("some salt")]
    [InlineData("salt to taste")]
    public void Preserves_word_quantities_verbatim(string input)
    {
        var parsed = IngredientLineParser.Parse(input, KnownUnits);

        parsed.Quantity.Should().BeNull();
        parsed.UnitText.Should().BeNull();
        parsed.Name.Should().Be(input);
    }

    [Fact]
    public void Empty_null_and_whitespace_lines_return_empty_name()
    {
        foreach (var input in new[] { null, string.Empty, "   ", "\t" })
        {
            var parsed = IngredientLineParser.Parse(input, KnownUnits);
            parsed.Quantity.Should().BeNull();
            parsed.UnitText.Should().BeNull();
            parsed.Name.Should().BeEmpty();
            parsed.PreparationNote.Should().BeNull();
        }
    }

    [Fact]
    public void Preserves_unicode_names_without_normalization()
    {
        var parsed = IngredientLineParser.Parse("1 jalape\u00F1o, minced", KnownUnits);

        parsed.Quantity.Should().Be(1m);
        parsed.Name.Should().Be("jalape\u00F1o");
        parsed.PreparationNote.Should().Be("minced");
    }

    [Fact]
    public void Quantity_only_line_has_empty_name()
    {
        var parsed = IngredientLineParser.Parse("1\u00BD", KnownUnits);

        parsed.Quantity.Should().Be(1.5m);
        parsed.UnitText.Should().BeNull();
        parsed.Name.Should().BeEmpty();
    }

    [Fact]
    public void Mixed_number_overflow_never_throws_and_stays_raw()
    {
        var max = decimal.MaxValue.ToString(CultureInfo.InvariantCulture);
        string[] inputs =
        [
            $"{max} 1/2 cups flour",
            $"{max} \u00BD cups flour",
            $"9 {max}/1 cups flour",
        ];

        foreach (var input in inputs)
        {
            var parsed = IngredientLineParser.Parse(input, KnownUnits);
            parsed.Quantity.Should().BeNull("overflowing input '{0}' must not be guessed", input);
            parsed.UnitText.Should().BeNull();
            parsed.Name.Should().Be(input);
        }
    }

    [Theory]
    [InlineData("1/0 cups flour")]
    [InlineData("1/99999999999999999999999999999999999 cups flour")]
    [InlineData("1/2/3 cups flour")]
    [InlineData("1.2.3 cups flour")]
    [InlineData("1..2 cups flour")]
    [InlineData("1.5/2 cups flour")]
    public void Malformed_numeric_tokens_stay_unparseable(string input)
    {
        var parsed = IngredientLineParser.Parse(input, KnownUnits);

        parsed.Quantity.Should().BeNull("malformed input '{0}' must not degrade to a prefix", input);
        parsed.UnitText.Should().BeNull();
        parsed.Name.Should().Be(input);
    }
}
