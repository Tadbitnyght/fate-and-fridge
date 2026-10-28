using System.Globalization;
using FateAndFridge.Api.Services;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit matrix for the household clock
/// household-local dates across DST and offset changes, the three M14 zones
/// and the frozen historical boundary that keeps
/// history immutable across timezone changes in either direction.
/// </summary>
public sealed class HouseholdClockTests
{
    private const string Chicago = "America/Chicago";
    private const string Auckland = "Pacific/Auckland";
    private const string Kolkata = "Asia/Kolkata";

    private static DateTimeOffset Instant(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static DateOnly Date(string value) =>
        DateOnly.Parse(value, CultureInfo.InvariantCulture);

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

    [Theory]
    [InlineData(Chicago)]
    [InlineData(Auckland)]
    [InlineData(Kolkata)]
    public void ResolveTimeZone_resolves_the_requested_zone_not_a_silent_utc_fallback(
        string timeZoneId)
    {
        var timeZone = HouseholdClock.ResolveTimeZone(timeZoneId);

        // A missing-tzdata UTC fallback must fail loudly here, not silently
        // shift the household's "today".
        timeZone.Id.Should().Be(timeZoneId);
        timeZone.Should().NotBe(TimeZoneInfo.Utc);
    }

    [Theory]
    // Winter/summer offsets prove the tzdb rules, not a fixed hand-written
    // offset: Chicago -6/-5, Auckland +13/+12, Kolkata +5:30 all year.
    [InlineData(Chicago, "2026-01-15T12:00:00Z", -360)]
    [InlineData(Chicago, "2026-07-15T12:00:00Z", -300)]
    [InlineData(Auckland, "2026-01-15T12:00:00Z", 780)]
    [InlineData(Auckland, "2026-07-15T12:00:00Z", 720)]
    [InlineData(Kolkata, "2026-01-15T12:00:00Z", 330)]
    [InlineData(Kolkata, "2026-07-15T12:00:00Z", 330)]
    public void ResolveTimeZone_yields_the_real_zone_offset(
        string timeZoneId,
        string instant,
        int expectedOffsetMinutes)
    {
        var timeZone = HouseholdClock.ResolveTimeZone(timeZoneId);

        timeZone.Id.Should().Be(timeZoneId);
        timeZone.GetUtcOffset(Instant(instant)).TotalMinutes.Should().Be(expectedOffsetMinutes);
    }

    [Theory]
    // 23:59 and 00:01 household-local, with the UTC calendar date deliberately
    // different on at least one side per zone: a UTC-naive implementation
    // returns the wrong "today" for that case.
    [InlineData(Chicago, "2026-11-16T05:59:00Z", "2026-11-15", "2026-11-16")]
    [InlineData(Chicago, "2026-11-16T06:01:00Z", "2026-11-16", "2026-11-16")]
    [InlineData(Auckland, "2026-11-15T10:59:00Z", "2026-11-15", "2026-11-15")]
    [InlineData(Auckland, "2026-11-15T11:01:00Z", "2026-11-16", "2026-11-15")]
    [InlineData(Kolkata, "2026-11-15T18:29:00Z", "2026-11-15", "2026-11-15")]
    [InlineData(Kolkata, "2026-11-15T18:31:00Z", "2026-11-16", "2026-11-15")]
    public void GetLocalDate_uses_the_local_midnight_boundary(
        string timeZoneId,
        string instant,
        string expectedLocalDate,
        string expectedUtcDate)
    {
        var timeZone = HouseholdClock.ResolveTimeZone(timeZoneId);
        var at = Instant(instant);

        HouseholdClock.GetLocalDate(timeZone, at).Should().Be(Date(expectedLocalDate));
        DateOnly.FromDateTime(at.UtcDateTime).Should().Be(Date(expectedUtcDate));
    }

    [Fact]
    public void Chicago_spring_forward_skips_the_nonexistent_0230_hour()
    {
        var timeZone = HouseholdClock.ResolveTimeZone(Chicago);

        // Real 2026 tzdata: 2026-03-08 08:00Z is 02:00 CST -> 03:00 CDT.
        timeZone.IsInvalidTime(new DateTime(2026, 3, 8, 2, 30, 0)).Should().BeTrue();
        timeZone.GetUtcOffset(Instant("2026-03-08T07:59:59Z")).TotalMinutes.Should().Be(-360);
        timeZone.GetUtcOffset(Instant("2026-03-08T08:00:00Z")).TotalMinutes.Should().Be(-300);

        var afterGap = TimeZoneInfo.ConvertTime(Instant("2026-03-08T08:30:00Z"), timeZone);
        afterGap.Hour.Should().Be(3);
        afterGap.Minute.Should().Be(30);
        HouseholdClock.GetLocalDate(timeZone, Instant("2026-03-08T08:30:00Z"))
            .Should().Be(Date("2026-03-08"));
    }

    [Fact]
    public void Chicago_fall_back_keeps_both_instants_of_the_duplicated_0130_hour()
    {
        var timeZone = HouseholdClock.ResolveTimeZone(Chicago);

        // Real 2026 tzdata: 2026-11-01 07:00Z is 01:59:59 CDT -> 01:00 CST.
        timeZone.IsAmbiguousTime(new DateTime(2026, 11, 1, 1, 30, 0)).Should().BeTrue();

        var first = TimeZoneInfo.ConvertTime(Instant("2026-11-01T06:30:00Z"), timeZone);
        var second = TimeZoneInfo.ConvertTime(Instant("2026-11-01T07:30:00Z"), timeZone);
        first.Hour.Should().Be(1);
        first.Minute.Should().Be(30);
        first.Offset.TotalMinutes.Should().Be(-300);
        second.Hour.Should().Be(1);
        second.Minute.Should().Be(30);
        second.Offset.TotalMinutes.Should().Be(-360);
        HouseholdClock.GetLocalDate(timeZone, Instant("2026-11-01T06:30:00Z"))
            .Should().Be(Date("2026-11-01"));
        HouseholdClock.GetLocalDate(timeZone, Instant("2026-11-01T07:30:00Z"))
            .Should().Be(Date("2026-11-01"));
    }

    [Fact]
    public void Auckland_spring_forward_gap_and_fall_back_duplicate_are_real_tzdata()
    {
        var timeZone = HouseholdClock.ResolveTimeZone(Auckland);

        // Real 2026 tzdata: DST starts 2026-09-27 14:00Z (02:00 NZST -> 03:00
        // NZDT) and ends 2026-04-05 14:00Z (02:59:59 NZDT -> 02:00 NZST). Those
        // local dates are 2026-09-27 and 2026-04-05; the matching UTC instants
        // are one day earlier because Auckland is east of UTC.
        timeZone.IsInvalidTime(new DateTime(2026, 9, 27, 2, 30, 0)).Should().BeTrue();
        timeZone.IsAmbiguousTime(new DateTime(2026, 4, 5, 2, 30, 0)).Should().BeTrue();

        var afterGap = TimeZoneInfo.ConvertTime(Instant("2026-09-26T14:30:00Z"), timeZone);
        afterGap.Hour.Should().Be(3);
        afterGap.Offset.TotalMinutes.Should().Be(780);

        var first = TimeZoneInfo.ConvertTime(Instant("2026-04-04T13:30:00Z"), timeZone);
        var second = TimeZoneInfo.ConvertTime(Instant("2026-04-04T14:30:00Z"), timeZone);
        first.Hour.Should().Be(2);
        first.Minute.Should().Be(30);
        first.Offset.TotalMinutes.Should().Be(780);
        second.Hour.Should().Be(2);
        second.Minute.Should().Be(30);
        second.Offset.TotalMinutes.Should().Be(720);
    }

    [Fact]
    public void Kolkata_has_a_fixed_half_hour_offset_and_no_dst_transition()
    {
        var timeZone = HouseholdClock.ResolveTimeZone(Kolkata);

        timeZone.IsInvalidTime(new DateTime(2026, 3, 8, 2, 30, 0)).Should().BeFalse();
        timeZone.IsAmbiguousTime(new DateTime(2026, 11, 1, 1, 30, 0)).Should().BeFalse();
        timeZone.GetUtcOffset(Instant("2026-01-15T12:00:00Z")).TotalMinutes.Should().Be(330);
        timeZone.GetUtcOffset(Instant("2026-07-15T12:00:00Z")).TotalMinutes.Should().Be(330);
        HouseholdClock.GetLocalDate(timeZone, Instant("2026-11-15T18:29:00Z"))
            .Should().Be(Date("2026-11-15"));
        HouseholdClock.GetLocalDate(timeZone, Instant("2026-11-15T18:31:00Z"))
            .Should().Be(Date("2026-11-16"));
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

    [Theory]
    // Boundary moved forward (the old local day became the frozen boundary)
    // the boundary date is historical even when it is still "today".
    [InlineData("2026-11-15", "2026-11-15", "2026-11-15", true)]
    [InlineData("2026-11-15", "2026-11-15", "2026-11-16", false)]
    // Boundary moved backward (a timezone change made the local date earlier)
    // the stored boundary is ahead of today and must keep everything at or
    // before it frozen.
    [InlineData("2026-11-13", "2026-11-15", "2026-11-13", true)]
    [InlineData("2026-11-13", "2026-11-15", "2026-11-14", true)]
    [InlineData("2026-11-13", "2026-11-15", "2026-11-15", true)]
    [InlineData("2026-11-13", "2026-11-15", "2026-11-16", false)]
    // The boundary never regresses: an older stored boundary still freezes its
    // own date and every earlier one.
    [InlineData("2026-11-13", "2026-11-10", "2026-11-10", true)]
    [InlineData("2026-11-13", "2026-11-10", "2026-11-11", true)]
    [InlineData("2026-11-13", "2026-11-10", "2026-11-13", false)]
    public void A_frozen_boundary_keeps_history_historical_in_both_directions(
        string today,
        string boundary,
        string date,
        bool expectedHistorical)
    {
        var context = new HouseholdCalendarContext(
            Today: Date(today),
            HistoricalBoundaryDate: Date(boundary),
            WeekStartsOn: 0,
            DefaultDinerCount: 4,
            TimeZone: TimeZoneInfo.Utc);

        HouseholdClock.IsHistorical(Date(date), context).Should().Be(expectedHistorical);
    }
}
