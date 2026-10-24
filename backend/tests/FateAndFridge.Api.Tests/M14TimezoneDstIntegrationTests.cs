using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M14-04 three-zone timezone/DST battery (docs/M14_PLAN.md §1.8, §4;
/// docs/TESTING.md §4; docs/ARCHITECTURE.md §11): <c>America/Chicago</c>
/// (deployment default, northern DST), <c>Pacific/Auckland</c> (southern DST)
/// and <c>Asia/Kolkata</c> (+05:30, no DST), across the "today" boundary, the
/// calendar past/future/lock rules, the statistics month/year windows, the
/// meal-history range and month browser, the randomizer repeat/preview/apply
/// targets, the audit/backup timestamps and the frozen historical boundary.
/// Every "now" comes from the injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// Real 2026 tzdata transitions (verified with <c>zdump -v</c>):
/// Chicago spring-forward 2026-03-08 08:00Z (02:00 CST -> 03:00 CDT) and
/// fall-back 2026-11-01 07:00Z (01:59:59 CDT -> 01:00 CST); Auckland DST ends
/// 2026-04-05 14:00Z (02:59:59 NZDT -> 02:00 NZST) and starts 2026-09-27
/// 14:00Z (01:59:59 NZST -> 03:00 NZDT); Kolkata is a fixed +05:30.
/// </remarks>
[Trait("Category", "Integration")]
[Collection(M11StatisticsCollection.Name)]
public sealed class M14TimezoneDstIntegrationTests(M11StatisticsFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string OverviewPath = "/api/v1/statistics/overview";
    private const string RecipeMetricsPath = "/api/v1/statistics/recipes";
    private const string HistoryPath = "/api/v1/statistics/history";
    private const string AuditPath = "/api/v1/admin/audit";
    private const string BackupsPath = "/api/v1/admin/backups";
    private const string HouseholdSettingsPath = "/api/v1/settings/household";
    private const string Chicago = "America/Chicago";
    private const string Auckland = "Pacific/Auckland";
    private const string Kolkata = "Asia/Kolkata";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Zones => new() { Chicago, Auckland, Kolkata };

    public async ValueTask InitializeAsync()
    {
        // M11StatisticsFixture does not reset drafts or the backup catalog, and
        // backup_metadata references users with RESTRICT, so clear both first.
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM calendar_reroll_drafts; DELETE FROM backup_metadata;",
                Ct);
        }

        await fixture.ResetAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- 1. The "today" boundary -----------------------------------------

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Today_boundary_is_household_local_at_2359_and_0001(string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var clock = new TestTimeProvider(SeedTimestamp);
        using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var localDate = new DateOnly(2026, 11, 15);
        var cases = new[]
        {
            (Time: new TimeOnly(23, 59), Expected: localDate),
            (Time: new TimeOnly(0, 1), Expected: localDate.AddDays(1)),
        };

        var sawUtcDateMismatch = false;
        foreach (var (time, expectedToday) in cases)
        {
            var instant = AtLocal(zone, expectedToday, time);
            clock.UtcNow = instant;

            var utcDate = DateOnly.FromDateTime(instant.UtcDateTime);
            sawUtcDateMismatch |= utcDate != expectedToday;

            // The requested zone must be the resolved zone: a silent UTC
            // fallback would make these assertions fail.
            HouseholdClock.GetLocalDate(HouseholdClock.ResolveTimeZone(zone), instant)
                .Should().Be(expectedToday);

            var overview = await GetOverviewAsync(client);
            var windows = overview.GetProperty("windows");
            windows.GetProperty("today").GetString().Should().Be(Format(expectedToday));
            windows.GetProperty("timezone").GetString().Should().Be(zone);

            var range = await GetCalendarRangeAsync(client, expectedToday, expectedToday);
            range.GetProperty("today").GetString().Should().Be(Format(expectedToday));
            // Range reads return only non-blank days; the day read carries the
            // today/editability flags.
            range.GetProperty("entries").GetArrayLength().Should().Be(0);
            var day = await GetCalendarDayAsync(client, expectedToday);
            day.GetProperty("date").GetString().Should().Be(Format(expectedToday));
            day.GetProperty("is_today").GetBoolean().Should().BeTrue();
            day.GetProperty("can_edit").GetBoolean().Should().BeTrue();

            var history = await GetHistoryAsync(client);
            history.GetProperty("range").GetProperty("end").GetString()
                .Should().Be(Format(expectedToday));
            history.GetProperty("range").GetProperty("timezone").GetString().Should().Be(zone);
        }

        // At least one side of the local midnight pair is on a different UTC
        // calendar date for every required zone, so a UTC-naive "today" fails.
        sawUtcDateMismatch.Should().BeTrue(
            "each required zone must exercise a UTC date that differs from the local date");
    }

    // ---- 2. Calendar past/future/lock rules ------------------------------

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Calendar_past_future_and_lock_rules_follow_the_local_date(string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var recipe = await SeedRecipeAsync("Timezone Casserole");
        var today = new DateOnly(2026, 11, 15);

        // Chicago is west of UTC, so 23:59 local is already the next UTC day;
        // Auckland and Kolkata are east, so 00:01 local is still the previous
        // UTC day. Each case proves the boundary is not the UTC date.
        var instant = zone == Chicago
            ? AtLocal(zone, today, new TimeOnly(23, 59, 30))
            : AtLocal(zone, today, new TimeOnly(0, 1, 0));
        DateOnly.FromDateTime(instant.UtcDateTime).Should().NotBe(today);

        var clock = new TestTimeProvider(instant);
        using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        await AssertErrorCodeAsync(
            await PutEntryAsync(client, today.AddDays(-1)),
            ErrorCodes.CalendarDateInPast);
        await AssertErrorCodeAsync(
            await DeleteEntryAsync(client, today.AddDays(-1)),
            ErrorCodes.CalendarDateInPast);
        await AssertErrorCodeAsync(
            await PostAsync(client, $"{CalendarPath}/{Format(today.AddDays(-1))}/lock"),
            ErrorCodes.CalendarDateInPast);
        await AssertErrorCodeAsync(
            await PostAsync(client, $"{CalendarPath}/{Format(today.AddDays(-1))}/reroll"),
            ErrorCodes.CalendarDateInPast);

        // Today is editable and the (future) next day is editable.
        (await PutEntryAsync(client, today)).StatusCode.Should().Be(HttpStatusCode.OK);
        var todayDay = await GetCalendarDayAsync(client, today);
        todayDay.GetProperty("is_today").GetBoolean().Should().BeTrue();
        todayDay.GetProperty("is_historical").GetBoolean().Should().BeFalse();
        todayDay.GetProperty("can_edit").GetBoolean().Should().BeTrue();

        var reroll = await PostAsync(client, $"{CalendarPath}/{Format(today)}/reroll");
        reroll.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(reroll)).GetProperty("entry").GetProperty("recipe")
            .GetProperty("id").GetInt64().Should().Be(recipe.Id);

        var lockToday = await PostAsync(client, $"{CalendarPath}/{Format(today)}/lock");
        lockToday.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(lockToday)).GetProperty("entry").GetProperty("is_locked")
            .GetBoolean().Should().BeTrue();

        var future = today.AddDays(1);
        (await PutEntryAsync(client, future)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(client, $"{CalendarPath}/{Format(future)}/lock")).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await DeleteEntryAsync(client, future)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await DeleteEntryAsync(client, today)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ---- 3. Statistics month/year windows --------------------------------

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Statistics_month_and_year_use_the_local_month_at_0030_on_the_first(
        string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var first = new DateOnly(2026, 12, 1);
        await SeedEntryAsync(new DateOnly(2026, 11, 30), CalendarEntryType.EatOut);
        await SeedEntryAsync(first, CalendarEntryType.EatOut);
        await SeedEntryAsync(new DateOnly(2026, 12, 31), CalendarEntryType.Holiday);
        await SeedEntryAsync(new DateOnly(2027, 1, 1), CalendarEntryType.Misc);

        var instant = AtLocal(zone, first, new TimeOnly(0, 30));
        // Auckland/Kolkata are still on 2026-11-30 in UTC at 00:30 local; a
        // UTC-naive implementation reports November.
        if (zone != Chicago)
        {
            DateOnly.FromDateTime(instant.UtcDateTime).Should().Be(new DateOnly(2026, 11, 30));
        }

        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();
        var overview = await GetOverviewAsync(client);

        var windows = overview.GetProperty("windows");
        windows.GetProperty("today").GetString().Should().Be("2026-12-01");
        windows.GetProperty("month").GetProperty("start").GetString().Should().Be("2026-12-01");
        windows.GetProperty("month").GetProperty("end").GetString().Should().Be("2026-12-31");
        windows.GetProperty("year").GetProperty("start").GetString().Should().Be("2026-01-01");
        windows.GetProperty("year").GetProperty("end").GetString().Should().Be("2026-12-31");

        overview.GetProperty("meal_count_month").GetInt32().Should().Be(2);
        overview.GetProperty("meal_count_year").GetInt32().Should().Be(3);
        overview.GetProperty("special_event_counts").EnumerateArray()
            .Single(row => row.GetProperty("entry_type").GetString() == "holiday")
            .GetProperty("count").GetInt32().Should().Be(1);
        overview.GetProperty("special_event_counts").EnumerateArray()
            .Single(row => row.GetProperty("entry_type").GetString() == "misc")
            .GetProperty("count").GetInt32().Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Statistics_eaten_split_uses_the_local_today(string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var recipe = await SeedRecipeAsync("Boundary Stew");
        var first = new DateOnly(2026, 12, 1);
        await SeedEntryAsync(new DateOnly(2026, 11, 30), CalendarEntryType.Recipe, recipe.Id, true, recipe.Name);
        await SeedEntryAsync(first, CalendarEntryType.Recipe, recipe.Id, true, recipe.Name);
        await SeedEntryAsync(first.AddDays(1), CalendarEntryType.Recipe, recipe.Id, true, recipe.Name);

        var instant = AtLocal(zone, first, new TimeOnly(0, 30));
        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();

        var overview = await GetOverviewAsync(client);
        var ranked = overview.GetProperty("most_eaten").EnumerateArray().Single();
        ranked.GetProperty("times_eaten").GetInt32().Should().Be(1);
        ranked.GetProperty("last_eaten").GetString().Should().Be("2026-11-30");

        var metrics = await GetRecipeMetricsAsync(client, recipe.Id);
        metrics.GetProperty("last_eaten").GetString().Should().Be("2026-11-30");
        metrics.GetProperty("next_planned").GetString().Should().Be("2026-12-01");
        metrics.GetProperty("times_eaten").GetInt32().Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Statistics_year_rolls_over_at_local_new_year(string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var newYear = new DateOnly(2027, 1, 1);
        await SeedEntryAsync(new DateOnly(2026, 12, 31), CalendarEntryType.Holiday);
        await SeedEntryAsync(newYear, CalendarEntryType.EatOut);
        await SeedEntryAsync(new DateOnly(2027, 1, 31), CalendarEntryType.EatOut);
        await SeedEntryAsync(new DateOnly(2027, 12, 31), CalendarEntryType.Holiday);
        await SeedEntryAsync(new DateOnly(2028, 1, 1), CalendarEntryType.Holiday);

        var instant = AtLocal(zone, newYear, new TimeOnly(0, 30));
        // The eastern zones are still on 2026-12-31 in UTC; a UTC-naive year
        // window would be 2026, not 2027.
        if (zone != Chicago)
        {
            DateOnly.FromDateTime(instant.UtcDateTime).Should().Be(new DateOnly(2026, 12, 31));
        }

        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();
        var overview = await GetOverviewAsync(client);

        var windows = overview.GetProperty("windows");
        windows.GetProperty("today").GetString().Should().Be("2027-01-01");
        windows.GetProperty("month").GetProperty("start").GetString().Should().Be("2027-01-01");
        windows.GetProperty("month").GetProperty("end").GetString().Should().Be("2027-01-31");
        windows.GetProperty("year").GetProperty("start").GetString().Should().Be("2027-01-01");
        windows.GetProperty("year").GetProperty("end").GetString().Should().Be("2027-12-31");

        overview.GetProperty("meal_count_month").GetInt32().Should().Be(2);
        overview.GetProperty("meal_count_year").GetInt32().Should().Be(3);
        overview.GetProperty("special_event_counts").EnumerateArray()
            .Single(row => row.GetProperty("entry_type").GetString() == "holiday")
            .GetProperty("count").GetInt32().Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Statistics_month_window_holds_at_2330_on_the_last_day(string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var lastDay = new DateOnly(2027, 1, 31);
        await SeedEntryAsync(new DateOnly(2026, 12, 31), CalendarEntryType.Holiday);
        await SeedEntryAsync(new DateOnly(2027, 1, 1), CalendarEntryType.EatOut);
        await SeedEntryAsync(lastDay, CalendarEntryType.EatOut);
        await SeedEntryAsync(new DateOnly(2027, 2, 1), CalendarEntryType.EatOut);

        var instant = AtLocal(zone, lastDay, new TimeOnly(23, 30));
        // Chicago is west of UTC, so 23:30 local is already 2027-02-01 in UTC;
        // a UTC-naive month window would be February.
        if (zone == Chicago)
        {
            DateOnly.FromDateTime(instant.UtcDateTime).Should().Be(new DateOnly(2027, 2, 1));
        }

        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();
        var overview = await GetOverviewAsync(client);

        overview.GetProperty("windows").GetProperty("today").GetString()
            .Should().Be("2027-01-31");
        overview.GetProperty("windows").GetProperty("month").GetProperty("start").GetString()
            .Should().Be("2027-01-01");
        overview.GetProperty("windows").GetProperty("month").GetProperty("end").GetString()
            .Should().Be("2027-01-31");
        overview.GetProperty("meal_count_month").GetInt32().Should().Be(2);
        overview.GetProperty("meal_count_year").GetInt32().Should().Be(3);
    }

    // ---- 4. Meal-history ranges and month grouping -----------------------

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Meal_history_range_and_month_grouping_use_the_local_month(string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var first = new DateOnly(2026, 12, 1);
        await SeedEntryAsync(new DateOnly(2026, 11, 15), CalendarEntryType.EatOut);
        await SeedEntryAsync(new DateOnly(2026, 11, 30), CalendarEntryType.Holiday);
        await SeedEntryAsync(first, CalendarEntryType.EatOut);

        var instant = AtLocal(zone, first, new TimeOnly(0, 30));
        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();

        var november = await GetHistoryAsync(
            client,
            "?start=2026-11-01&end=2026-11-30&month=2026-11");
        var range = november.GetProperty("range");
        range.GetProperty("start").GetString().Should().Be("2026-11-01");
        range.GetProperty("end").GetString().Should().Be("2026-11-30");
        range.GetProperty("timezone").GetString().Should().Be(zone);

        var month = november.GetProperty("month");
        month.GetProperty("key").GetString().Should().Be("2026-11");
        month.GetProperty("start").GetString().Should().Be("2026-11-01");
        month.GetProperty("end").GetString().Should().Be("2026-11-30");
        november.GetProperty("days").EnumerateArray()
            .Select(day => day.GetProperty("date").GetString())
            .Should().Equal("2026-11-30", "2026-11-15");

        // The default page is the newest month in the range that holds a
        // strictly-past entry (November), even though the range end is the
        // household-local today in December.
        var defaultPage = await GetHistoryAsync(client);
        defaultPage.GetProperty("range").GetProperty("end").GetString()
            .Should().Be("2026-12-01");
        defaultPage.GetProperty("month").GetProperty("key").GetString().Should().Be("2026-11");
        defaultPage.GetProperty("days").EnumerateArray()
            .Select(day => day.GetProperty("date").GetString())
            .Should().Equal("2026-11-30", "2026-11-15");
        defaultPage.GetProperty("paging").GetProperty("newer").GetString().Should().Be("2026-12");
        defaultPage.GetProperty("paging").GetProperty("older").GetString().Should().Be("2026-10");

        var december = await GetHistoryAsync(client, "?month=2026-12");
        december.GetProperty("month").GetProperty("start").GetString()
            .Should().Be("2026-12-01");
        december.GetProperty("month").GetProperty("end").GetString()
            .Should().Be("2026-12-31");
        december.GetProperty("days").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public async Task Meal_history_excludes_todays_entry_at_2330_on_the_last_day(string zone)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var lastDay = new DateOnly(2026, 11, 30);
        await SeedEntryAsync(new DateOnly(2026, 11, 29), CalendarEntryType.EatOut);
        await SeedEntryAsync(lastDay, CalendarEntryType.EatOut);

        var instant = AtLocal(zone, lastDay, new TimeOnly(23, 30));
        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();

        var history = await GetHistoryAsync(client);
        history.GetProperty("range").GetProperty("end").GetString().Should().Be("2026-11-30");
        history.GetProperty("month").GetProperty("key").GetString().Should().Be("2026-11");
        history.GetProperty("days").EnumerateArray()
            .Select(day => day.GetProperty("date").GetString())
            .Should().Equal("2026-11-29");
    }

    // ---- 5. Randomizer windows and batch targets -------------------------

    [Theory]
    // Real DST transition days: Chicago fall-back/spring-forward and Auckland
    // spring-forward/fall-back, plus a no-DST Kolkata control.
    [InlineData(Chicago, "2026-11-01")]
    [InlineData(Chicago, "2026-03-08")]
    [InlineData(Auckland, "2026-09-27")]
    [InlineData(Auckland, "2026-04-05")]
    [InlineData(Kolkata, "2026-11-15")]
    public async Task Repeat_check_window_is_local_date_inclusive_across_dst(
        string zone,
        string dateText)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var recipe = await SeedRecipeAsync("Window Curry");
        var date = Date(dateText);
        await SeedEntryAsync(date.AddDays(-7), CalendarEntryType.Recipe, recipe.Id, true, recipe.Name);
        await SeedEntryAsync(date.AddDays(-8), CalendarEntryType.Recipe, recipe.Id, true, recipe.Name);
        await SeedEntryAsync(date.AddDays(14), CalendarEntryType.Recipe, recipe.Id, true, recipe.Name);
        await SeedEntryAsync(date.AddDays(15), CalendarEntryType.Recipe, recipe.Id, true, recipe.Name);

        var instant = AtLocal(zone, date, new TimeOnly(0, 30));
        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"{CalendarPath}/{dateText}/repeat-check?recipe_id={recipe.Id}",
            Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);

        root.GetProperty("window_start").GetString().Should().Be(Format(date.AddDays(-7)));
        root.GetProperty("window_end").GetString().Should().Be(Format(date.AddDays(14)));
        root.GetProperty("is_repeat").GetBoolean().Should().BeTrue();
        root.GetProperty("occurrences").EnumerateArray()
            .Select(row => row.GetProperty("date").GetString())
            .Should().BeEquivalentTo(Format(date.AddDays(-7)), Format(date.AddDays(14)));

        // The windows are anchored to the household-local today, not UTC.
        await AssertErrorCodeAsync(
            await PutEntryAsync(client, date.AddDays(-1)),
            ErrorCodes.CalendarDateInPast);
        (await PutEntryAsync(client, date)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    // Seven-day previews straddling each real 2026 transition, so the 23-hour
    // and 25-hour local days are enumerated exactly once.
    [InlineData(Chicago, "2026-03-06")]
    [InlineData(Chicago, "2026-10-30")]
    [InlineData(Auckland, "2026-04-03")]
    [InlineData(Auckland, "2026-09-25")]
    [InlineData(Kolkata, "2026-11-13")]
    public async Task Seven_day_preview_enumerates_each_local_date_once(string zone, string startText)
    {
        await SeedSingleUserHouseholdAsync(zone);
        await SeedRecipeAsync("Preview Pie");
        var start = Date(startText);

        using var factory = CreateFactory(
            new TestTimeProvider(AtLocal(zone, start, new TimeOnly(0, 30))));
        using var client = factory.CreateClient();

        var response = await PreviewAsync(client, start, BatchRerollService.ModeSevenDays);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);

        root.GetProperty("draft").GetProperty("start_date").GetString().Should().Be(startText);
        root.GetProperty("draft").GetProperty("end_date").GetString()
            .Should().Be(Format(start.AddDays(6)));

        var dates = root.GetProperty("days").EnumerateArray()
            .Select(day => day.GetProperty("date").GetString())
            .ToArray();
        dates.Should().Equal(Enumerable.Range(0, 7).Select(offset => Format(start.AddDays(offset))));
        dates.Distinct().Should().HaveCount(7);
        root.GetProperty("days").EnumerateArray()
            .Should().OnlyContain(day => day.GetProperty("status").GetString() == "proposed");
    }

    [Theory]
    [InlineData(Chicago, "2026-03-08")]
    [InlineData(Chicago, "2026-11-01")]
    [InlineData(Auckland, "2026-04-05")]
    [InlineData(Auckland, "2026-09-27")]
    public async Task Rest_of_month_preview_ends_on_the_local_last_day(string zone, string startText)
    {
        await SeedSingleUserHouseholdAsync(zone);
        await SeedRecipeAsync("Month End Pie");
        var start = Date(startText);
        var last = new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));

        using var factory = CreateFactory(
            new TestTimeProvider(AtLocal(zone, start, new TimeOnly(0, 30))));
        using var client = factory.CreateClient();

        var response = await PreviewAsync(client, start, BatchRerollService.ModeRestOfMonth);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);

        root.GetProperty("draft").GetProperty("end_date").GetString().Should().Be(Format(last));
        var dates = root.GetProperty("days").EnumerateArray()
            .Select(day => day.GetProperty("date").GetString())
            .ToArray();
        var expectedCount = last.DayNumber - start.DayNumber + 1;
        dates.Should().HaveCount(expectedCount);
        dates.Distinct().Should().HaveCount(expectedCount);
        dates[0].Should().Be(startText);
        dates[^1].Should().Be(Format(last));
    }

    [Fact]
    public async Task Batch_apply_rechecks_the_local_midnight_boundary()
    {
        await SeedSingleUserHouseholdAsync(Chicago);
        await SeedRecipeAsync("Midnight Pie");
        var day = new DateOnly(2026, 11, 1); // 25-hour fall-back day
        var clock = new TestTimeProvider(AtLocal(Chicago, day, new TimeOnly(23, 59, 30)));
        using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        var preview = await PreviewAsync(client, day, BatchRerollService.ModeSevenDays);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewRoot = await ReadJsonAsync(preview);
        var token = previewRoot.GetProperty("draft").GetProperty("token").GetString();
        previewRoot.GetProperty("days").EnumerateArray().First()
            .GetProperty("date").GetString().Should().Be("2026-11-01");

        // Cross household-local midnight: 2026-11-02 00:01 CST.
        clock.UtcNow = AtLocal(Chicago, day.AddDays(1), new TimeOnly(0, 1, 30));

        var apply = await PostJsonAsync(
            client,
            $"{CalendarPath}/reroll/apply",
            JsonSerializer.Serialize(new { token }));
        apply.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        await AssertErrorCodeAsync(apply, ErrorCodes.CalendarDateInPast);

        var afterMidnight = await PreviewAsync(
            client,
            day.AddDays(1),
            BatchRerollService.ModeSevenDays);
        afterMidnight.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(afterMidnight)).GetProperty("draft")
            .GetProperty("start_date").GetString().Should().Be("2026-11-02");
        (await GetOverviewAsync(client)).GetProperty("windows")
            .GetProperty("today").GetString().Should().Be("2026-11-02");
    }

    // ---- 6. Audit and backup timestamps ----------------------------------

    [Fact]
    public async Task Audit_timestamps_are_utc_and_unchanged_by_the_household_zone()
    {
        await SeedSingleUserHouseholdAsync(Chicago);
        // 2026-11-01 01:30 occurs twice in Chicago: 06:30Z (CDT) and 07:30Z
        // (CST). The seeds use the local wall-clock offsets; the API must render
        // every instant as unambiguous UTC regardless of the household zone.
        var first = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        var second = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-6));
        var nextDay = new DateTimeOffset(2026, 11, 1, 12, 30, 0, TimeSpan.Zero);
        await SeedAuditAsync("calendar.updated", first, "m14-tz");
        await SeedAuditAsync("calendar.updated", second, "m14-tz");
        await SeedAuditAsync("calendar.updated", nextDay, "m14-tz");

        using var factory = CreateFactory(new TestTimeProvider(second));
        using var client = factory.CreateClient();

        var chicagoItems = await GetAuditItemsAsync(client);
        var chicagoStrings = TimestampStrings(chicagoItems);
        chicagoStrings.Should().HaveCount(3);
        AssertUtcTimestamp(chicagoStrings[0], nextDay);
        AssertUtcTimestamp(chicagoStrings[1], second);
        AssertUtcTimestamp(chicagoStrings[2], first);

        // The same instants are rendered identically under a southern-hemisphere
        // zone: timestamps are instants, never household-wall-clock text.
        await SetTimezoneAsync(Auckland);
        var aucklandStrings = TimestampStrings(await GetAuditItemsAsync(client));
        aucklandStrings.Should().Equal(chicagoStrings);

        // The local-day filter follows the household zone: 12:30Z is still
        // 2026-11-01 in Chicago but already 2026-11-02 in Auckland.
        await SetTimezoneAsync(Chicago);
        var chicagoDay = await GetAuditItemsAsync(client, "?from=2026-11-01&to=2026-11-01");
        chicagoDay.Should().HaveCount(3);
        await SetTimezoneAsync(Auckland);
        var aucklandDay = await GetAuditItemsAsync(client, "?from=2026-11-01&to=2026-11-01");
        aucklandDay.Should().HaveCount(2);
    }

    [Fact]
    public async Task Backup_timestamps_are_utc_and_unchanged_by_the_household_zone()
    {
        await SeedSingleUserHouseholdAsync(Chicago);
        var first = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5));
        var second = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-6));
        var firstBackup = await SeedBackupAsync("m14-first.ffbackup", first);
        var secondBackup = await SeedBackupAsync("m14-second.ffbackup", second);

        using var factory = CreateFactory(new TestTimeProvider(second));
        using var client = factory.CreateClient();

        var chicago = await GetBackupsAsync(client);
        var chicagoRows = chicago.Where(row =>
            row.GetProperty("filename").GetString() is "m14-first.ffbackup" or "m14-second.ffbackup")
            .ToArray();
        chicagoRows.Should().HaveCount(2);
        // Newest instant first, even though both are 01:30 local wall time.
        chicagoRows[0].GetProperty("id").GetInt64().Should().Be(secondBackup.Id);
        chicagoRows[1].GetProperty("id").GetInt64().Should().Be(firstBackup.Id);
        var chicagoStrings = chicagoRows
            .Select(row => row.GetProperty("created_at").GetString()!)
            .ToArray();
        AssertUtcTimestamp(chicagoStrings[0], second);
        AssertUtcTimestamp(chicagoStrings[1], first);

        await SetTimezoneAsync(Auckland);
        var aucklandRows = (await GetBackupsAsync(client)).Where(row =>
            row.GetProperty("filename").GetString() is "m14-first.ffbackup" or "m14-second.ffbackup")
            .ToArray();
        aucklandRows.Select(row => row.GetProperty("created_at").GetString())
            .Should().Equal(chicagoStrings);
    }

    [Fact]
    public void Backup_file_names_are_always_utc()
    {
        // 2026-01-01 10:00 at +13:00 is still 2025-12-31 in UTC.
        var createdAt = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(13));

        BackupNaming.ForManualBackup("1.2.3", createdAt)
            .Should().Be("fatefridge-1.2.3-20251231T210000Z.ffbackup");
    }

    // ---- 7. DST transition days ------------------------------------------

    [Theory]
    // 03:30 is just after the spring-forward gap; 01:30 (Chicago) and 02:30
    // (Auckland) are inside the duplicated fall-back hour. GetUtcOffset picks a
    // valid standard-time instant, which must still be the same local date.
    [InlineData(Chicago, "2026-03-08", "03:30")]
    [InlineData(Chicago, "2026-11-01", "01:30")]
    [InlineData(Auckland, "2026-04-05", "02:30")]
    [InlineData(Auckland, "2026-09-27", "03:30")]
    public async Task Each_dst_transition_day_is_one_local_calendar_date(
        string zone,
        string dayText,
        string localTime)
    {
        await SeedSingleUserHouseholdAsync(zone);
        var day = Date(dayText);
        await SeedEntryAsync(day.AddDays(-1), CalendarEntryType.EatOut);
        await SeedEntryAsync(day, CalendarEntryType.EatOut);
        await SeedEntryAsync(day.AddDays(1), CalendarEntryType.EatOut);

        var instant = AtLocal(zone, day, TimeOnly.Parse(localTime, CultureInfo.InvariantCulture));
        using var factory = CreateFactory(new TestTimeProvider(instant));
        using var client = factory.CreateClient();

        var range = await GetCalendarRangeAsync(client, day.AddDays(-1), day.AddDays(1));
        range.GetProperty("today").GetString().Should().Be(dayText);
        var dates = range.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("date").GetString())
            .ToArray();
        dates.Should().Equal(Format(day.AddDays(-1)), dayText, Format(day.AddDays(1)));
        dates.Distinct().Should().HaveCount(3);

        var today = await GetCalendarDayAsync(client, day);
        today.GetProperty("is_today").GetBoolean().Should().BeTrue();
        today.GetProperty("is_historical").GetBoolean().Should().BeFalse();
        (await PutEntryAsync(client, day)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(client, $"{CalendarPath}/{dayText}/lock")).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await GetOverviewAsync(client)).GetProperty("windows").GetProperty("today").GetString()
            .Should().Be(dayText);
    }

    // ---- 8. Frozen historical boundary -----------------------------------

    [Fact]
    public async Task Frozen_boundary_never_regresses_across_timezone_changes()
    {
        // Timezone changes are driven by the injected clock, so a synthetic
        // backward tick lets us exercise the "boundary ahead of today" branch
        // without touching the host clock.
        var seed = new DateTimeOffset(2026, 3, 2, 2, 0, 0, TimeSpan.Zero);
        await SeedSingleUserHouseholdAsync(Auckland);
        var clock = new TestTimeProvider(seed);
        using var factory = CreateFactory(clock);
        using var client = factory.CreateClient();

        // Auckland 2026-03-02 15:00 (NZDT); Chicago 2026-03-01 20:00 (CST).
        (await GetOverviewAsync(client)).GetProperty("windows").GetProperty("today").GetString()
            .Should().Be("2026-03-02");
        (await PatchTimezoneAsync(client, Chicago)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBoundaryAsync()).Should().Be(new DateOnly(2026, 3, 1));

        // The old Auckland day (2026-03-02) is now future and editable; the
        // frozen 2026-03-01 is today in Chicago but must stay read-only.
        (await GetOverviewAsync(client)).GetProperty("windows").GetProperty("today").GetString()
            .Should().Be("2026-03-01");
        (await GetCalendarDayAsync(client, new DateOnly(2026, 3, 1)))
            .GetProperty("is_historical").GetBoolean().Should().BeTrue();
        await AssertErrorCodeAsync(
            await PutEntryAsync(client, new DateOnly(2026, 3, 1)),
            ErrorCodes.CalendarDateInPast);
        (await PutEntryAsync(client, new DateOnly(2026, 3, 2))).StatusCode.Should().Be(HttpStatusCode.OK);

        // A later change moves the boundary forward, never backward.
        clock.UtcNow = new DateTimeOffset(2026, 3, 5, 2, 0, 0, TimeSpan.Zero);
        (await PatchTimezoneAsync(client, Auckland)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBoundaryAsync()).Should().Be(new DateOnly(2026, 3, 3));
        (await GetOverviewAsync(client)).GetProperty("windows").GetProperty("today").GetString()
            .Should().Be("2026-03-05");

        clock.UtcNow = new DateTimeOffset(2026, 3, 5, 4, 0, 0, TimeSpan.Zero);
        (await PatchTimezoneAsync(client, Chicago)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBoundaryAsync()).Should().Be(new DateOnly(2026, 3, 4));
        (await GetOverviewAsync(client)).GetProperty("windows").GetProperty("today").GetString()
            .Should().Be("2026-03-04");
        (await GetCalendarDayAsync(client, new DateOnly(2026, 3, 4)))
            .GetProperty("is_today").GetBoolean().Should().BeTrue();
        (await GetCalendarDayAsync(client, new DateOnly(2026, 3, 4)))
            .GetProperty("is_historical").GetBoolean().Should().BeTrue();
        await AssertErrorCodeAsync(
            await PutEntryAsync(client, new DateOnly(2026, 3, 4)),
            ErrorCodes.CalendarDateInPast);

        // A timezone change whose computed freeze date is older must not lower
        // the stored boundary.
        clock.UtcNow = new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);
        (await PatchTimezoneAsync(client, Auckland)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadBoundaryAsync()).Should().Be(new DateOnly(2026, 3, 4));
        (await GetOverviewAsync(client)).GetProperty("windows").GetProperty("today").GetString()
            .Should().Be("2026-03-05");
        await AssertErrorCodeAsync(
            await PutEntryAsync(client, new DateOnly(2026, 3, 4)),
            ErrorCodes.CalendarDateInPast);
        (await PutEntryAsync(client, new DateOnly(2026, 3, 5))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Helpers ---------------------------------------------------------

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private static string Format(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly Date(string value) =>
        DateOnly.Parse(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset AtLocal(string zone, DateOnly date, TimeOnly time)
    {
        var timeZone = HouseholdClock.ResolveTimeZone(zone);
        timeZone.Id.Should().Be(zone);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        timeZone.IsInvalidTime(local).Should().BeFalse(
            $"{local:O} must be a valid local time in {zone}");
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    private async Task SeedSingleUserHouseholdAsync(string zone, DateOnly? boundary = null)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M14 Timezone Household",
            Timezone = zone,
            HistoricalBoundaryDate = boundary,
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.SingleUser,
            RandomizerRepeatLookbackDays = 7,
            RandomizerRepeatLookaheadDays = 14,
            RandomizerRepeatMultiplier = 0.10m,
            RandomizerSpecialDayMultiplier = 0.02m,
            RandomizerProteinMultiplier = 0.70m,
            RandomizerProteinNeighborDistance = 1,
            RandomizerFavoriteMultiplier = 1.10m,
            RandomizerSpacingNearDays = 3,
            RandomizerSpacingFarDays = 8,
            RandomizerSpacingSearchDays = 90,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {SeedTimestamp})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            Ct);
    }

    private async Task<Recipe> SeedRecipeAsync(string name)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            Status = RecipeStatus.Normal,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(Ct);
        return recipe;
    }

    private async Task SeedEntryAsync(
        DateOnly date,
        CalendarEntryType entryType,
        long? recipeId = null,
        bool includeGroceries = false,
        string? snapshotRecipeName = null)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = entryType,
            RecipeId = recipeId,
            IncludeGroceries = includeGroceries,
            SnapshotRecipeName = snapshotRecipeName,
            SnapshotDisplayText = snapshotRecipeName ?? "M14 entry",
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<AuditEvent> SeedAuditAsync(
        string eventType,
        DateTimeOffset at,
        string entityId)
    {
        await using var context = fixture.CreateContext();
        var row = new AuditEvent
        {
            EventType = eventType,
            EntityType = "calendar_entry",
            EntityId = entityId,
            // Npgsql 10 only binds zero-offset DateTimeOffsets to timestamptz;
            // the app always writes an instant, so normalize the seed.
            CreatedAt = at.ToUniversalTime(),
            MetadataJson = "{}",
        };
        context.AuditEvents.Add(row);
        await context.SaveChangesAsync(Ct);
        return row;
    }

    private async Task<BackupMetadata> SeedBackupAsync(string filename, DateTimeOffset at)
    {
        await using var context = fixture.CreateContext();
        var row = new BackupMetadata
        {
            Filename = filename,
            AppVersion = "0.1.0",
            SchemaVersion = "test",
            CreatedAt = at.ToUniversalTime(),
            Checksum = new string('a', 64),
            SizeBytes = 1,
            Kind = BackupKind.Manual,
            Status = BackupStatus.Completed,
        };
        context.BackupMetadata.Add(row);
        await context.SaveChangesAsync(Ct);
        return row;
    }

    private async Task<DateOnly?> ReadBoundaryAsync()
    {
        await using var context = fixture.CreateContext();
        return (await context.HouseholdSettings.AsNoTracking().SingleAsync(Ct))
            .HistoricalBoundaryDate;
    }

    private async Task SetTimezoneAsync(string zone)
    {
        await using var context = fixture.CreateContext();
        var settings = await context.HouseholdSettings.SingleAsync(Ct);
        settings.Timezone = zone;
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }

    private static Task<HttpResponseMessage> PutEntryAsync(HttpClient client, DateOnly date) =>
        SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(date)}",
            """{"entry_type":"eat_out","restaurant_name":"M14 Diner"}""");

    private static Task<HttpResponseMessage> DeleteEntryAsync(HttpClient client, DateOnly date) =>
        SendJsonAsync(client, HttpMethod.Delete, $"{CalendarPath}/{Format(date)}");

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path) =>
        SendJsonAsync(client, HttpMethod.Post, path);

    private static Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string path,
        string json) =>
        SendJsonAsync(client, HttpMethod.Post, path, json);

    private static Task<HttpResponseMessage> PatchTimezoneAsync(HttpClient client, string zone) =>
        SendJsonAsync(
            client,
            HttpMethod.Patch,
            HouseholdSettingsPath,
            JsonSerializer.Serialize(new { timezone = zone }));

    private static Task<HttpResponseMessage> PreviewAsync(
        HttpClient client,
        DateOnly start,
        string mode) =>
        PostJsonAsync(
            client,
            $"{CalendarPath}/reroll/preview",
            JsonSerializer.Serialize(new { start_date = Format(start), mode }));

    private static async Task<JsonElement> GetOverviewAsync(HttpClient client)
    {
        var response = await client.GetAsync(OverviewPath, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> GetRecipeMetricsAsync(HttpClient client, long recipeId)
    {
        var response = await client.GetAsync($"{RecipeMetricsPath}/{recipeId}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> GetHistoryAsync(HttpClient client, string query = "")
    {
        var response = await client.GetAsync($"{HistoryPath}{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> GetCalendarRangeAsync(
        HttpClient client,
        DateOnly start,
        DateOnly end)
    {
        var response = await client.GetAsync(
            $"{CalendarPath}?start={Format(start)}&end={Format(end)}",
            Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> GetCalendarDayAsync(HttpClient client, DateOnly date)
    {
        var response = await client.GetAsync($"{CalendarPath}/{Format(date)}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static async Task<IReadOnlyList<JsonElement>> GetAuditItemsAsync(
        HttpClient client,
        string query = "")
    {
        var response = await client.GetAsync($"{AuditPath}{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        return root.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("entity_id").GetString() == "m14-tz")
            .Select(item => item.Clone())
            .ToArray();
    }

    private static async Task<IReadOnlyList<JsonElement>> GetBackupsAsync(HttpClient client)
    {
        var response = await client.GetAsync(BackupsPath, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static string[] TimestampStrings(IReadOnlyList<JsonElement> items) =>
        items.Select(item => item.GetProperty("recorded_at").GetString()!).ToArray();

    private static void AssertUtcTimestamp(string text, DateTimeOffset expected)
    {
        (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
            .Should().BeTrue($"{text} must be an unambiguous UTC ISO-8601 timestamp");

        DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed).Should().BeTrue();
        parsed.Offset.Should().Be(TimeSpan.Zero);
        parsed.ToUniversalTime().Should().Be(expected.ToUniversalTime());
    }

    private static async Task AssertErrorCodeAsync(
        HttpResponseMessage response,
        string expectedCode)
    {
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var root = await ReadJsonAsync(response);
        root.GetProperty("error").GetProperty("code").GetString().Should().Be(expectedCode);
    }
}
