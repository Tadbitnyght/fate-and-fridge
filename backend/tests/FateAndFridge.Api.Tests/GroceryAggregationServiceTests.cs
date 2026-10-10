using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Aggregation and display battery for M8-02 (docs/M8_PLAN.md §1.4–§1.6, §4;
/// docs/PRODUCT_SPEC.md §20–§22): compatible-dimension conversion, incompatible
/// separation, count/unitless combining, largest-present-unit selection, exact
/// fractions vs two decimals, per-meal attribution, deterministic ordering, and
/// the defensive unconvertible-contribution path.
/// </summary>
public sealed class GroceryAggregationServiceTests
{
    private static readonly IReadOnlyDictionary<string, ConversionUnit> SeedUnits =
        ReferenceDataSeedCatalog.Units.ToDictionary(
            unit => unit.Symbol,
            unit => new ConversionUnit(null, unit.Symbol, unit.Dimension, unit.BaseFactor),
            StringComparer.Ordinal);

    private readonly GroceryAggregationService _service = new(new UnitConversionService());

    private static ConversionUnit Unit(string symbol) => SeedUnits[symbol];

    private static GroceryIngredientInput Ingredient(
        long ingredientId,
        string name,
        decimal? quantity,
        string? unit,
        long recipeId,
        string recipeName,
        int day = 6) =>
        new(
            ingredientId,
            name,
            quantity,
            unit is null ? null : Unit(unit),
            recipeId,
            recipeName,
            new DateOnly(2026, 10, day));

    // ---- Aggregation ---------------------------------------------------------

    [Fact]
    public void Compatible_mass_units_combine_into_the_largest_present_unit()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Ground Beef", 1m, "lb", 10, "Tacos", day: 6),
            Ingredient(1, "Ground Beef", 8m, "oz", 20, "Meatloaf", day: 8),
        ]);

        items.Should().HaveCount(1);
        var item = items[0];
        item.Dimension.Should().Be(UnitDimension.Mass);
        item.Quantity.Should().Be(1.5m);
        item.UnitSymbol.Should().Be("lb");
        item.QuantityText.Should().Be("1 1/2");
        item.Sources.Should().HaveCount(2);
    }

    [Fact]
    public void Compatible_volume_units_combine_into_the_largest_present_unit()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Olive Oil", 4m, "tbsp", 10, "A", day: 6),
            Ingredient(1, "Olive Oil", 0.25m, "cup", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Dimension.Should().Be(UnitDimension.Volume);
        items[0].Quantity.Should().Be(0.5m);
        items[0].UnitSymbol.Should().Be("cup");
        items[0].QuantityText.Should().Be("1/2");
    }

    [Fact]
    public void Incompatible_dimensions_stay_separate()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Onion", 1m, "each", 10, "A", day: 6),
            Ingredient(1, "Onion", 0.5m, "cup", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(2);
        items.Should().ContainSingle(item =>
            item.Dimension == UnitDimension.Count && item.UnitSymbol == "each" && item.Quantity == 1m);
        items.Should().ContainSingle(item =>
            item.Dimension == UnitDimension.Volume && item.UnitSymbol == "cup" && item.Quantity == 0.5m);
        items.Select(item => item.IdentityHash).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Mass_and_volume_of_one_ingredient_stay_separate()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Cheddar", 8m, "oz", 10, "A", day: 6),
            Ingredient(1, "Cheddar", 0.5m, "cup", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(2);
        items.Select(item => item.Dimension).Should().BeEquivalentTo(
            new[] { UnitDimension.Mass, UnitDimension.Volume });
    }

    [Fact]
    public void Unitless_quantities_combine_by_count()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Egg", 2m, null, 10, "A", day: 6),
            Ingredient(1, "Egg", 3m, null, 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Dimension.Should().Be(UnitDimension.Count);
        items[0].Quantity.Should().Be(5m);
        items[0].UnitSymbol.Should().BeNull();
        items[0].QuantityText.Should().Be("5");
    }

    [Fact]
    public void Same_count_unit_combines_by_count()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Garlic", 1m, "clove", 10, "A", day: 6),
            Ingredient(1, "Garlic", 2m, "clove", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Quantity.Should().Be(3m);
        items[0].UnitSymbol.Should().Be("clove");
        items[0].QuantityText.Should().Be("3");
    }

    [Fact]
    public void Different_count_units_stay_separate()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Tortilla", 1m, "each", 10, "A", day: 6),
            Ingredient(1, "Tortilla", 1m, "package", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(2);
        items.Select(item => item.UnitSymbol).Should().BeEquivalentTo(new[] { "each", "package" });
        items.Select(item => item.IdentityHash).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Unitless_counts_stay_separate_from_an_explicit_count_unit()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Egg", 2m, null, 10, "A", day: 6),
            Ingredient(1, "Egg", 1m, "each", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(2);
        items.Select(item => item.UnitSymbol).Should().BeEquivalentTo(new string?[] { null, "each" });
        items.Select(item => item.IdentityHash).Should().OnlyHaveUniqueItems();
    }

    // ---- Display -------------------------------------------------------------

    [Fact]
    public void A_single_present_unit_is_used_as_is()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Ground Beef", 8m, "oz", 10, "A", day: 6),
        ]);

        items.Should().HaveCount(1);
        items[0].UnitSymbol.Should().Be("oz");
        items[0].Quantity.Should().Be(8m);
        items[0].QuantityText.Should().Be("8");
    }

    [Fact]
    public void The_largest_unit_present_in_the_bucket_is_selected()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Flour", 100m, "g", 10, "A", day: 6),
            Ingredient(1, "Flour", 1m, "kg", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].UnitSymbol.Should().Be("kg");
        items[0].Quantity.Should().Be(1.1m);
        items[0].QuantityText.Should().Be("1.1");
    }

    [Fact]
    public void Exact_quantities_render_as_common_fractions()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Sugar", 2m, "cup", 10, "A", day: 6),
            Ingredient(1, "Sugar", 0.25m, "cup", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].QuantityText.Should().Be("2 1/4");
    }

    [Theory]
    [InlineData(0.3, "0.3")]
    [InlineData(1.234, "1.23")]
    [InlineData(0.33, "0.33")]
    public void Non_fraction_quantities_render_with_up_to_two_decimals(decimal quantity, string expected)
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Sugar", quantity, "cup", 10, "A", day: 6),
        ]);

        items.Should().HaveCount(1);
        items[0].QuantityText.Should().Be(expected);
    }

    [Fact]
    public void Quantity_less_nonconvertible_lines_render_without_a_quantity()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Salt", null, "to taste", 10, "A", day: 6),
        ]);

        items.Should().HaveCount(1);
        items[0].Dimension.Should().Be(UnitDimension.Nonconvertible);
        items[0].Quantity.Should().BeNull();
        items[0].QuantityText.Should().BeEmpty();
        items[0].UnitSymbol.Should().Be("to taste");
    }

    [Fact]
    public void Quantity_less_unitless_lines_render_without_a_quantity()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Salt", null, null, 10, "A", day: 6),
        ]);

        items.Should().HaveCount(1);
        items[0].Dimension.Should().Be(UnitDimension.Count);
        items[0].Quantity.Should().BeNull();
        items[0].QuantityText.Should().BeEmpty();
        items[0].UnitSymbol.Should().BeNull();
    }

    // ---- Attribution ---------------------------------------------------------

    [Fact]
    public void Attribution_is_one_row_per_contributing_meal()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Ground Beef", 1m, "lb", 10, "Tacos", day: 6),
            // Same recipe and date, a second ingredient line: one source, but the
            // quantity still counts.
            Ingredient(1, "Ground Beef", 1m, "lb", 10, "Tacos", day: 6),
            // Same recipe, a different meal date: a second source.
            Ingredient(1, "Ground Beef", 1m, "lb", 10, "Tacos", day: 7),
            // Different recipe on the first date: a third source.
            Ingredient(1, "Ground Beef", 1m, "lb", 20, "Meatloaf", day: 6),
        ]);

        items.Should().HaveCount(1);
        items[0].Quantity.Should().Be(4m);
        items[0].Sources.Should().HaveCount(3);
        items[0].Sources.Select(source => (source.RecipeId, source.MealDate))
            .Should().OnlyHaveUniqueItems();
        items[0].Sources.Select(source => source.MealDate).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Nonconvertible_lines_merge_their_sources_without_quantities()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Salt", null, "to taste", 10, "A", day: 6),
            Ingredient(1, "Salt", null, "to taste", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Quantity.Should().BeNull();
        items[0].Sources.Should().HaveCount(2);
    }

    [Fact]
    public void A_quantity_less_mass_contribution_joins_the_combined_line()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Ground Beef", 1m, "lb", 10, "A", day: 6),
            Ingredient(1, "Ground Beef", null, "lb", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Quantity.Should().Be(1m);
        items[0].Sources.Should().HaveCount(2);
    }

    // ---- Identity and determinism --------------------------------------------

    [Fact]
    public void Aggregated_identity_matches_GroceryItemIdentity()
    {
        var items = _service.Aggregate(
        [
            Ingredient(7, "Cheddar", 8m, "oz", 10, "A", day: 6),
        ]);

        items[0].IdentityHash.Should()
            .Be(GroceryItemIdentity.ForRecipeItem(7, UnitDimension.Mass, null, null));
    }

    [Fact]
    public void Lines_sharing_a_canonical_ingredient_combine_across_display_names()
    {
        var items = _service.Aggregate(
        [
            Ingredient(5, "Scallion", 2m, "each", 10, "A", day: 6),
            Ingredient(5, "Green Onion", 3m, "each", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Quantity.Should().Be(5m);
        items[0].Name.Should().Be("Green Onion");
    }

    [Fact]
    public void Different_canonical_ingredients_with_the_same_name_stay_separate()
    {
        var items = _service.Aggregate(
        [
            Ingredient(5, "Onion", 1m, "each", 10, "A", day: 6),
            Ingredient(6, "Onion", 1m, "each", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(2);
        items.Select(item => item.IdentityHash).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Output_is_ordered_by_ingredient_name()
    {
        var items = _service.Aggregate(
        [
            Ingredient(1, "Zucchini", 1m, "each", 10, "A", day: 6),
            Ingredient(2, "Apples", 1m, "each", 20, "B", day: 6),
            Ingredient(3, "Milk", 1m, "gallon", 30, "C", day: 6),
        ]);

        items.Select(item => item.Name).Should().Equal("Apples", "Milk", "Zucchini");
    }

    [Fact]
    public void Empty_input_produces_no_items()
    {
        _service.Aggregate([]).Should().BeEmpty();
    }

    // ---- Defensive conversion ------------------------------------------------

    [Fact]
    public void An_unconvertible_mass_contribution_keeps_its_own_line()
    {
        var mystery = new ConversionUnit(99, "mystery", UnitDimension.Mass, null);

        var items = _service.Aggregate(
        [
            Ingredient(1, "Ground Beef", 1m, "lb", 10, "Tacos", day: 6),
            new GroceryIngredientInput(
                1, "Ground Beef", 2m, mystery, 20, "Chili", new DateOnly(2026, 10, 7)),
        ]);

        items.Should().HaveCount(2);
        items.Should().ContainSingle(item => item.UnitSymbol == "lb" && item.Quantity == 1m);
        items.Should().ContainSingle(item => item.UnitSymbol == "mystery" && item.Quantity == 2m);

        // The fallback line is deliberately never dropped; it reuses the dimension
        // identity so household check state survives the factor being configured.
        items.Select(item => item.IdentityHash).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public void Unconvertible_contributions_of_the_same_unit_still_combine()
    {
        var mystery = new ConversionUnit(99, "mystery", UnitDimension.Mass, null);

        var items = _service.Aggregate(
        [
            new GroceryIngredientInput(
                1, "Ground Beef", 2m, mystery, 10, "Tacos", new DateOnly(2026, 10, 6)),
            new GroceryIngredientInput(
                1, "Ground Beef", 3m, mystery, 20, "Chili", new DateOnly(2026, 10, 7)),
        ]);

        items.Should().HaveCount(1);
        items[0].UnitSymbol.Should().Be("mystery");
        items[0].Quantity.Should().Be(5m);
        items[0].Sources.Should().HaveCount(2);
    }
}
