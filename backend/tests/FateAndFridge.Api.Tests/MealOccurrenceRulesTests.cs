using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Unit matrix for the shared statistics occurrence rules
/// (docs/M11_PLAN.md §1.3, §1.4, §1.20): the strictly-past eaten rule and the
/// recipe-id-or-normalized-snapshot grouping identity that the overview,
/// per-recipe metrics, and history reads all share.
/// </summary>
public sealed class MealOccurrenceRulesTests
{
    [Fact]
    public void Only_dates_strictly_before_household_today_are_eaten()
    {
        var today = new DateOnly(2026, 3, 15);

        MealOccurrenceRules.IsEaten(new DateOnly(2026, 3, 14), today).Should().BeTrue();
        MealOccurrenceRules.IsEaten(today, today).Should().BeFalse();
        MealOccurrenceRules.IsEaten(new DateOnly(2026, 3, 16), today).Should().BeFalse();
    }

    [Fact]
    public void The_occurrence_predicate_is_the_include_groceries_flag()
    {
        var predicate = MealOccurrenceRules.IsRecipeOccurrence.Compile();

        predicate(new CalendarEntry { IncludeGroceries = true, SnapshotDisplayText = string.Empty })
            .Should().BeTrue();
        predicate(new CalendarEntry { IncludeGroceries = false, SnapshotDisplayText = string.Empty })
            .Should().BeFalse();
    }

    [Fact]
    public void A_recipe_id_wins_the_group_key()
    {
        MealOccurrenceRules.GroupKey(5, "Ignored snapshot").Should().Be("recipe:5");
    }

    [Fact]
    public void A_missing_recipe_id_groups_by_the_normalized_snapshot_name()
    {
        MealOccurrenceRules.GroupKey(null, "Grandma's   Stew").Should().Be("name:grandma's stew");
        MealOccurrenceRules.GroupKey(null, "Jalapeño").Should().Be("name:jalapeno");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_identity_has_no_group_key(string? snapshot)
    {
        MealOccurrenceRules.GroupKey(null, snapshot).Should().BeNull();
    }

    [Fact]
    public void Snapshot_display_keeps_case_and_diacritics_but_collapses_whitespace()
    {
        MealOccurrenceRules.SnapshotDisplayName("  Grandma's   Stew ").Should().Be("Grandma's Stew");
        MealOccurrenceRules.SnapshotDisplayName("Jalapeño").Should().Be("Jalapeño");
        MealOccurrenceRules.SnapshotDisplayName("   ").Should().BeNull();
    }
}
