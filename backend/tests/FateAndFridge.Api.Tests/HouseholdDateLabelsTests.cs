using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// The shared table-driven household date labels used by
/// the statistics windows and the meal-history month pages and paging cursors.
/// They must read the same in every deployment, including globalization-invariant
/// hosts where ICU-backed month names are empty.
/// </summary>
public sealed class HouseholdDateLabelsTests
{
    [Theory]
    [InlineData(2026, 1, 1, "January 2026")]
    [InlineData(2026, 10, 16, "October 2026")]
    [InlineData(2026, 12, 31, "December 2026")]
    [InlineData(2025, 2, 28, "February 2025")]
    public void Month_label_is_the_english_month_and_year(
        int year,
        int month,
        int day,
        string expected) =>
        HouseholdDateLabels.MonthLabel(new DateOnly(year, month, day)).Should().Be(expected);

    [Theory]
    [InlineData(2026, 1, 1, "2026-01")]
    [InlineData(2026, 10, 16, "2026-10")]
    [InlineData(2025, 12, 31, "2025-12")]
    public void Month_key_zero_pads_to_four_digit_year_and_two_digit_month(
        int year,
        int month,
        int day,
        string expected) =>
        HouseholdDateLabels.MonthKey(new DateOnly(year, month, day)).Should().Be(expected);

    [Fact]
    public void Year_label_is_the_four_digit_year() =>
        HouseholdDateLabels.YearLabel(new DateOnly(2026, 10, 16)).Should().Be("2026");
}
