using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Authoritative M2 default reference data, transcribed from
/// Values are kept exactly as documented; the seeder
/// normalizes every name and alias with <see cref="ReferenceDataNormalizer"/>
/// before insert, so no normalized form is hard-coded here.
/// </summary>
internal static class ReferenceDataSeedCatalog
{
    /// <summary>Departments in documented order; the list index sets <c>sort_order</c>.</summary>
    internal static IReadOnlyList<string> DepartmentNames { get; } =
    [
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
        "Other",
    ];

    /// <summary>Units in documented order; only mass/volume units carry a base factor.</summary>
    internal static IReadOnlyList<SeedUnit> Units { get; } =
    [
        new("tsp", "teaspoon", UnitDimension.Volume, 4.92892159375m, ["teaspoon", "teaspoons", "t"]),
        new("tbsp", "tablespoon", UnitDimension.Volume, 14.78676478125m, ["tablespoon", "tablespoons", "Tbsp", "T"]),
        new("fl oz", "fluid ounce", UnitDimension.Volume, 29.5735295625m, ["floz", "fl. oz.", "fluid ounce", "fluid ounces"]),
        new("cup", "cup", UnitDimension.Volume, 236.5882365m, ["cups"]),
        new("pint", "pint", UnitDimension.Volume, 473.176473m, ["pints", "pt"]),
        new("quart", "quart", UnitDimension.Volume, 946.352946m, ["quarts", "qt"]),
        new("gallon", "gallon", UnitDimension.Volume, 3785.411784m, ["gallons", "gal"]),
        new("oz", "ounce", UnitDimension.Mass, 28.349523125m, ["ounce", "ounces"]),
        new("lb", "pound", UnitDimension.Mass, 453.59237m, ["pound", "pounds", "lbs"]),
        new("g", "gram", UnitDimension.Mass, 1m, ["gram", "grams"]),
        new("kg", "kilogram", UnitDimension.Mass, 1000m, ["kilogram", "kilograms"]),
        new("each", "each", UnitDimension.Count, null, ["ea"]),
        new("package", "package", UnitDimension.Count, null, ["packages", "pkg"]),
        new("can", "can", UnitDimension.Count, null, ["cans"]),
        new("bottle", "bottle", UnitDimension.Count, null, ["bottles"]),
        new("jar", "jar", UnitDimension.Count, null, ["jars"]),
        new("bunch", "bunch", UnitDimension.Count, null, ["bunches"]),
        new("clove", "clove", UnitDimension.Count, null, ["cloves"]),
        new("slice", "slice", UnitDimension.Count, null, ["slices"]),
        new("pinch", "pinch", UnitDimension.Count, null, ["pinches"]),
        new("to taste", "to taste", UnitDimension.Nonconvertible, null, ["as needed"]),
    ];

    /// <summary>Proteins in documented order.</summary>
    internal static IReadOnlyList<string> ProteinNames { get; } =
    [
        "Beef",
        "Chicken",
        "Pork",
        "Turkey",
        "Seafood",
        "Lamb",
        "Venison",
        "Duck",
        "Other",
    ];
}

/// <summary>One documented unit plus its aliases.</summary>
internal sealed record SeedUnit(
    string Symbol,
    string DisplayName,
    UnitDimension Dimension,
    decimal? BaseFactor,
    IReadOnlyList<string> Aliases);
