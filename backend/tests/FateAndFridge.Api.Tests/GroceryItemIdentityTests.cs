using System.Security.Cryptography;
using System.Text;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Contract tests for the stable grocery identity
/// (docs/M8_PLAN.md §1.4, §1.10): canonical ingredient + dimension, never
/// quantity, preparation note, or the displayed mass/volume unit; count and
/// nonconvertible units are unit-specific; the canonical string format and its
/// SHA-256 hex rendering are pinned so M8-06 stores exactly the same value.
/// </summary>
public sealed class GroceryItemIdentityTests
{
    [Fact]
    public void Mass_identity_is_stable_across_units()
    {
        var pounds = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Mass, null, null);
        var ounces = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Mass, 7, "oz");

        ounces.Should().Be(pounds);
    }

    [Fact]
    public void Volume_identity_is_stable_across_units()
    {
        var cups = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Volume, null, null);
        var tablespoons = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Volume, 3, "tbsp");

        tablespoons.Should().Be(cups);
    }

    [Fact]
    public void Dimensions_never_collapse()
    {
        var mass = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Mass, null, null);
        var volume = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Volume, null, null);
        var count = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, null, null);
        var nonconvertible = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Nonconvertible, null, null);

        new[] { mass, volume, count, nonconvertible }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Count_identity_is_unit_specific()
    {
        var each = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, 1, "each");
        var package = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, 2, "package");
        var unitless = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, null, null);

        new[] { each, package, unitless }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Count_identity_prefers_the_persisted_id_over_the_symbol()
    {
        var bySymbol = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, 1, "each");
        var sameIdDifferentSymbol = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, 1, "ea");

        sameIdDifferentSymbol.Should().Be(bySymbol);
    }

    [Fact]
    public void Count_identity_falls_back_to_a_case_insensitive_symbol()
    {
        var lower = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, null, "each");
        var upper = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Count, null, "EACH");

        upper.Should().Be(lower);
    }

    [Fact]
    public void Different_ingredients_never_collapse()
    {
        var first = GroceryItemIdentity.ForRecipeItem(1, UnitDimension.Mass, null, null);
        var second = GroceryItemIdentity.ForRecipeItem(2, UnitDimension.Mass, null, null);

        second.Should().NotBe(first);
    }

    [Fact]
    public void Manual_identity_is_stable_and_distinct_from_a_recipe_identity()
    {
        var manual = GroceryItemIdentity.ForManualItem(7);

        GroceryItemIdentity.ForManualItem(7).Should().Be(manual);
        GroceryItemIdentity.ForManualItem(8).Should().NotBe(manual);
        GroceryItemIdentity.ForRecipeItem(7, UnitDimension.Count, null, null).Should().NotBe(manual);
    }

    [Fact]
    public void Hashes_are_lowercase_sha256_hex()
    {
        var hash = GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Mass, null, null);

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Canonical_format_is_pinned()
    {
        GroceryItemIdentity.CanonicalForRecipeItem(42, UnitDimension.Mass, null, null)
            .Should().Be("recipe|42|mass");
        GroceryItemIdentity.CanonicalForRecipeItem(42, UnitDimension.Volume, 1, "cup")
            .Should().Be("recipe|42|volume");
        GroceryItemIdentity.CanonicalForRecipeItem(42, UnitDimension.Count, 5, "cup")
            .Should().Be("recipe|42|count|id:5");
        GroceryItemIdentity.CanonicalForRecipeItem(42, UnitDimension.Count, null, "Each")
            .Should().Be("recipe|42|count|sym:each");
        GroceryItemIdentity.CanonicalForRecipeItem(42, UnitDimension.Count, null, null)
            .Should().Be("recipe|42|count|none");
        GroceryItemIdentity.CanonicalForRecipeItem(42, UnitDimension.Nonconvertible, 6, "to taste")
            .Should().Be("recipe|42|nonconvertible|id:6");
        GroceryItemIdentity.CanonicalForManualItem(7).Should().Be("manual|7");
    }

    [Fact]
    public void Hash_matches_the_sha256_of_the_canonical_string()
    {
        var expected = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("recipe|42|mass")))
            .ToLowerInvariant();

        GroceryItemIdentity.ForRecipeItem(42, UnitDimension.Mass, null, null).Should().Be(expected);
    }
}
