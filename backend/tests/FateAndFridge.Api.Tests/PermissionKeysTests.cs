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

    [Fact]
    public void Statistics_and_history_reads_introduce_no_eighth_permission_key()
    {
        // M11-07: the statistics and meal-history reads reuse recipes.view and
        // calendar.view; no statistics/history key exists (docs/M11_PLAN.md §1.7).
        PermissionKeys.All.Should().HaveCount(7);
        PermissionKeys.All.Should().NotContain(key =>
            key.StartsWith("statistics", StringComparison.Ordinal)
            || key.StartsWith("history", StringComparison.Ordinal));

        PermissionKeys.IsKnown("statistics.view").Should().BeFalse();
        PermissionKeys.IsKnown("statistics.edit").Should().BeFalse();
        PermissionKeys.IsKnown("history.view").Should().BeFalse();
    }

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
