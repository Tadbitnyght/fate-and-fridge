using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit matrix for the household clock (docs/M5_PLAN.md §1.7, §4):
/// household-local dates across DST and offset changes, and the frozen
/// historical boundary that keeps history immutable across timezone changes.
/// </summary>
public sealed class HouseholdClockTests
{
    [Theory]
    // America/Chicago: 2026-03-08 is the spring-forward day; 2026-11-01 falls back.
    [InlineData("America/Chicago", "2026-03-08T07:30:00Z", "2026-03-08")] // 01:30 CST
    [InlineData("America/Chicago", "2026-03-08T09:30:00Z", "2026-03-08")] // 04:30 CDT
    [InlineData("America/Chicago", "2026-11-01T05:30:00Z", "2026-11-01")] // 00:30 CDT
    [InlineData("America/Chicago", "2026-11-01T07:30:00Z", "2026-11-01")] // 01:30 CST
    // Across the date line the local date differs from UTC.
    [InlineData("Pacific/Kiritimati", "2026-11-15T12:00:00Z", "2026-11-16")]
    [InlineData("Pacific/Honolulu", "2026-11-15T12:00:00Z", "2026-11-15")]
    public void GetLocalDate_uses_the_household_timezone(
        string timeZoneId,
        string instant,
        string expectedDate)
    {
        var timeZone = HouseholdClock.ResolveTimeZone(timeZoneId);

        var date = HouseholdClock.GetLocalDate(
            timeZone,
            DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture));

        date.Should().Be(DateOnly.Parse(expectedDate, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ResolveTimeZone_falls_back_to_utc_for_unknown_ids()
    {
        HouseholdClock.ResolveTimeZone("Not/AZone").Should().Be(TimeZoneInfo.Utc);
        HouseholdClock.ResolveTimeZone(null).Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public void Past_and_today_are_classified_without_a_boundary()
    {
        var context = new HouseholdCalendarContext(
            Today: new DateOnly(2026, 11, 15),
            HistoricalBoundaryDate: null,
            WeekStartsOn: 0,
            DefaultDinerCount: 4,
            TimeZone: TimeZoneInfo.Utc);

        HouseholdClock.IsHistorical(new DateOnly(2026, 11, 14), context).Should().BeTrue();
        HouseholdClock.IsHistorical(new DateOnly(2026, 11, 15), context).Should().BeFalse();
        HouseholdClock.IsHistorical(new DateOnly(2026, 11, 16), context).Should().BeFalse();
    }

    [Fact]
    public void A_frozen_boundary_keeps_history_historical_even_when_it_is_today()
    {
        var context = new HouseholdCalendarContext(
            Today: new DateOnly(2026, 11, 15),
            HistoricalBoundaryDate: new DateOnly(2026, 11, 15),
            WeekStartsOn: 0,
            DefaultDinerCount: 4,
            TimeZone: TimeZoneInfo.Utc);

        // The 15th is today but was historical before the timezone change, so it
        // stays frozen and read-only.
        HouseholdClock.IsHistorical(new DateOnly(2026, 11, 15), context).Should().BeTrue();
        HouseholdClock.IsHistorical(new DateOnly(2026, 11, 16), context).Should().BeFalse();
    }
}
