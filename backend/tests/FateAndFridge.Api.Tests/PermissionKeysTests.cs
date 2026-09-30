using FateAndFridge.Api.Auth.Permissions;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Key registry completeness and canonical ordering (docs/DATABASE.md §2,
/// docs/ARCHITECTURE.md §10).
/// </summary>
public sealed class PermissionKeysTests
{
    private static readonly string[] ExpectedKeys =
    [
        "calendar.view",
        "calendar.edit",
        "groceries.view",
        "groceries.edit",
        "recipes.view",
        "recipes.edit",
        "reference_data.edit",
    ];

    [Fact]
    public void All_contains_the_seven_documented_keys_in_canonical_order() =>
        PermissionKeys.All.Should().Equal(ExpectedKeys);

    [Theory]
    [InlineData("calendar.view")]
    [InlineData("calendar.edit")]
    [InlineData("groceries.view")]
    [InlineData("groceries.edit")]
    [InlineData("recipes.view")]
    [InlineData("recipes.edit")]
    [InlineData("reference_data.edit")]
    public void IsKnown_accepts_documented_keys(string key) =>
        PermissionKeys.IsKnown(key).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("calendar")]
    [InlineData("calendar.delete")]
    [InlineData("CALENDAR.VIEW")]
    [InlineData(" calendar.view ")]
    public void IsKnown_rejects_unknown_or_mis_cased_keys(string? key) =>
        PermissionKeys.IsKnown(key).Should().BeFalse();
}
