using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Fast (no database) checks that the embedded M2 seed catalog matches the
/// authoritative lists exactly: counts, order, dimensions
/// factors, and aliases.
/// </summary>
public sealed class ReferenceDataSeedCatalogTests
{
    [Fact]
    public void Departments_are_the_documented_fourteen_in_order()
    {
        ReferenceDataSeedCatalog.DepartmentNames.Should().Equal(
            "Produce",
            "Meat & Seafood",
            "Dairy & Eggs",
            "Bakery",
            "Deli",
            "Frozen",
            "Canned Goods",
            "Dry Goods / Pantry",
            "Spices & Seasonings",
            "Condiments & Sauces",
            "Beverages",
            "Household",
            "Personal Care",
            "Other");
    }

    [Fact]
    public void Units_are_the_documented_twenty_one_with_dimensions_and_factors()
    {
        ReferenceDataSeedCatalog.Units.Should().HaveCount(21);
        ReferenceDataSeedCatalog.Units.Select(unit => unit.Symbol).Should().Equal(
            "tsp",
            "tbsp",
            "fl oz",
            "cup",
            "pint",
            "quart",
            "gallon",
            "oz",
            "lb",
            "g",
            "kg",
            "each",
            "package",
            "can",
            "bottle",
            "jar",
            "bunch",
            "clove",
            "slice",
            "pinch",
            "to taste");

        var expected = new (string Symbol, UnitDimension Dimension, decimal? Factor)[]
        {
            ("tsp", UnitDimension.Volume, 4.92892159375m),
            ("tbsp", UnitDimension.Volume, 14.78676478125m),
            ("fl oz", UnitDimension.Volume, 29.5735295625m),
            ("cup", UnitDimension.Volume, 236.5882365m),
            ("pint", UnitDimension.Volume, 473.176473m),
            ("quart", UnitDimension.Volume, 946.352946m),
            ("gallon", UnitDimension.Volume, 3785.411784m),
            ("oz", UnitDimension.Mass, 28.349523125m),
            ("lb", UnitDimension.Mass, 453.59237m),
            ("g", UnitDimension.Mass, 1m),
            ("kg", UnitDimension.Mass, 1000m),
            ("each", UnitDimension.Count, null),
            ("package", UnitDimension.Count, null),
            ("can", UnitDimension.Count, null),
            ("bottle", UnitDimension.Count, null),
            ("jar", UnitDimension.Count, null),
            ("bunch", UnitDimension.Count, null),
            ("clove", UnitDimension.Count, null),
            ("slice", UnitDimension.Count, null),
            ("pinch", UnitDimension.Count, null),
            ("to taste", UnitDimension.Nonconvertible, null),
        };

        foreach (var (symbol, dimension, factor) in expected)
        {
            var unit = ReferenceDataSeedCatalog.Units.Single(candidate => candidate.Symbol == symbol);
            unit.Dimension.Should().Be(dimension, "unit {0} has a fixed dimension", symbol);
            unit.BaseFactor.Should().Be(factor, "unit {0} has the documented base factor", symbol);
        }
    }

    [Fact]
    public void Every_unit_lists_the_documented_aliases()
    {
        ReferenceDataSeedCatalog.Units.Sum(unit => unit.Aliases.Count).Should().Be(38);

        var expected = new Dictionary<string, string[]>
        {
            ["tsp"] = ["teaspoon", "teaspoons", "t"],
            ["tbsp"] = ["tablespoon", "tablespoons", "Tbsp", "T"],
            ["fl oz"] = ["floz", "fl. oz.", "fluid ounce", "fluid ounces"],
            ["cup"] = ["cups"],
            ["pint"] = ["pints", "pt"],
            ["quart"] = ["quarts", "qt"],
            ["gallon"] = ["gallons", "gal"],
            ["oz"] = ["ounce", "ounces"],
            ["lb"] = ["pound", "pounds", "lbs"],
            ["g"] = ["gram", "grams"],
            ["kg"] = ["kilogram", "kilograms"],
            ["each"] = ["ea"],
            ["package"] = ["packages", "pkg"],
            ["can"] = ["cans"],
            ["bottle"] = ["bottles"],
            ["jar"] = ["jars"],
            ["bunch"] = ["bunches"],
            ["clove"] = ["cloves"],
            ["slice"] = ["slices"],
            ["pinch"] = ["pinches"],
            ["to taste"] = ["as needed"],
        };

        foreach (var (symbol, aliases) in expected)
        {
            ReferenceDataSeedCatalog.Units.Single(unit => unit.Symbol == symbol)
                .Aliases.Should().Equal(aliases);
        }
    }

    [Fact]
    public void Proteins_are_the_documented_nine_in_order()
    {
        ReferenceDataSeedCatalog.ProteinNames.Should().Equal(
            "Beef",
            "Chicken",
            "Pork",
            "Turkey",
            "Seafood",
            "Lamb",
            "Venison",
            "Duck",
            "Other");
    }

    [Fact]
    public void Every_documented_name_and_alias_normalizes_successfully()
    {
        foreach (var name in ReferenceDataSeedCatalog.DepartmentNames
            .Concat(ReferenceDataSeedCatalog.ProteinNames))
        {
            ReferenceDataNormalizer.Normalize(name)
                .Should().BeOfType<ReferenceDataNormalizationResult.Success>("{0} is a valid seed name", name);
        }

        foreach (var unit in ReferenceDataSeedCatalog.Units)
        {
            ReferenceDataNormalizer.Normalize(unit.DisplayName)
                .Should().BeOfType<ReferenceDataNormalizationResult.Success>(
                    "{0} is a valid unit display name", unit.DisplayName);

            foreach (var alias in unit.Aliases)
            {
                ReferenceDataNormalizer.Normalize(alias)
                    .Should().BeOfType<ReferenceDataNormalizationResult.Success>(
                        "{0} is a valid alias for {1}", alias, unit.Symbol);
            }
        }
    }
}
