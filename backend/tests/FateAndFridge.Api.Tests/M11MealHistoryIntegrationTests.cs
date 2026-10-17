using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the M11-03 meal-history endpoint
/// (docs/M11_PLAN.md §1.22, §1.23, §1.25, §1.26, §2.2): the bounded last-12-month
/// default range, the 366-day cap and the malformed-input rejections, the
/// month-grouped strictly-past day set with no blank filler, month selection and
/// the paging cursors, the server-side AND-combined filters, and the
/// <c>calendar.view</c> permission matrix. The clock is injected so the
/// household today is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M11StatisticsCollection.Name)]
public sealed class M11MealHistoryIntegrationTests(M11StatisticsFixture fixture) : IAsyncLifetime
{
    private const string HistoryPath = "/api/v1/statistics/history";
    private const string OverviewPath = "/api/v1/statistics/overview";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "meal-history-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC on a Sunday, so America/Chicago is the same calendar date and
    // comfortably after the real host date so an injected clock never expires a
    // session cookie. Household today is 2026-11-15 and the default range is
    // 2025-12-01..2026-11-15 (the household month plus the eleven before it).
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Default range and the empty household ---------------------------

    [Fact]
    public async Task Empty_history_defaults_to_the_last_twelve_months_and_the_newest_month()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var root = await GetHistoryAsync(client);

        var range = root.GetProperty("range");
        // The household's current month plus the eleven before it.
        range.GetProperty("start").GetString().Should().Be("2025-12-01");
        range.GetProperty("end").GetString().Should().Be("2026-11-15");
        range.GetProperty("timezone").GetString().Should().Be("America/Chicago");

        var month = root.GetProperty("month");
        month.GetProperty("key").GetString().Should().Be("2026-11");
        month.GetProperty("label").GetString().Should().Be("November 2026");
        month.GetProperty("start").GetString().Should().Be("2026-11-01");
        month.GetProperty("end").GetString().Should().Be("2026-11-30");

        root.GetProperty("days").GetArrayLength().Should().Be(0);

        // No month in the range has an entry: the fallback page is the range's
        // newest month (docs/M11_PLAN.md §1.26).
        root.GetProperty("paging").GetProperty("newer").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("paging").GetProperty("older").GetString().Should().Be("2026-10");
    }

    // ---- Strictly past, entry-only days ---------------------------------

    [Fact]
    public async Task Only_strictly_past_days_with_entries_appear_newest_first()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 2), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 14), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 16), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();
        var root = await GetHistoryAsync(client);

        root.GetProperty("month").GetProperty("key").GetString().Should().Be("2026-11");
        // Today and the future are not eaten history; the blank days between the
        // two meals produce no rows.
        Dates(root).Should().Equal("2026-11-14", "2026-11-02");
        root.GetProperty("days").GetArrayLength().Should().Be(2);
    }

    // ---- Day row shape ---------------------------------------------------

    [Fact]
    public async Task Day_rows_carry_the_entry_type_recipe_name_and_primary_protein()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var chicken = await SeedProteinAsync("Chicken");
        var beef = await SeedProteinAsync("Beef");
        var primary = chicken.Id < beef.Id ? chicken : beef;
        var tacos = await SeedRecipeAsync("Tacos");
        await SeedRecipeProteinAsync(tacos.Id, chicken.Id);
        await SeedRecipeProteinAsync(tacos.Id, beef.Id);
        await SeedEntryAsync(new DateOnly(2026, 10, 15), CalendarEntryType.Recipe, tacos.Id, true, "Tacos");
        await SeedEntryAsync(
            new DateOnly(2026, 10, 14),
            CalendarEntryType.EatOut,
            recipeId: null,
            includeGroceries: false,
            snapshotDisplayText: "Sushi place");

        using var client = factory.CreateClient();
        var days = Days(await GetHistoryAsync(client, "?month=2026-10"));

        days.Should().HaveCount(2);

        days[0].GetProperty("date").GetString().Should().Be("2026-10-15");
        days[0].GetProperty("entry_type").GetString().Should().Be("recipe");
        days[0].GetProperty("recipe_id").GetInt64().Should().Be(tacos.Id);
        days[0].GetProperty("name").GetString().Should().Be("Tacos");
        // Several proteins: the deterministic primary is the lowest protein id.
        days[0].GetProperty("protein").GetString().Should().Be(primary.Name);

        days[1].GetProperty("date").GetString().Should().Be("2026-10-14");
        days[1].GetProperty("entry_type").GetString().Should().Be("eat_out");
        days[1].GetProperty("recipe_id").ValueKind.Should().Be(JsonValueKind.Null);
        days[1].GetProperty("name").GetString().Should().Be("Sushi place");
        days[1].GetProperty("protein").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_tombstone_day_uses_the_stored_snapshot_name()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        await SeedEntryAsync(
            new DateOnly(2026, 10, 5),
            CalendarEntryType.Recipe,
            recipeId: null,
            includeGroceries: true,
            snapshotRecipeName: "Grandma's Stew");

        using var client = factory.CreateClient();
        var day = Days(await GetHistoryAsync(client, "?month=2026-10")).Single();

        day.GetProperty("date").GetString().Should().Be("2026-10-05");
        day.GetProperty("entry_type").GetString().Should().Be("recipe");
        day.GetProperty("recipe_id").ValueKind.Should().Be(JsonValueKind.Null);
        day.GetProperty("name").GetString().Should().Be("Grandma's Stew");
        day.GetProperty("protein").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_renamed_recipe_keeps_its_meal_and_the_stored_snapshot_stays_filterable()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Old Stew");
        await SeedEntryAsync(new DateOnly(2026, 10, 1), CalendarEntryType.Recipe, recipe.Id, true, "Old Stew");
        await RenameRecipeAsync(recipe.Id, "New Stew");

        using var client = factory.CreateClient();
        var day = Days(await GetHistoryAsync(client, "?month=2026-10")).Single();

        day.GetProperty("recipe_id").GetInt64().Should().Be(recipe.Id);
        day.GetProperty("name").GetString().Should().Be("New Stew");

        // The recipe filter matches the id or the normalized stored snapshot
        // name, so a renamed recipe's meal stays filterable by both.
        Dates(await GetHistoryAsync(client, $"?month=2026-10&recipe={recipe.Id}"))
            .Should().Equal("2026-10-01");
        Dates(await GetHistoryAsync(
                client,
                $"?month=2026-10&recipe={Uri.EscapeDataString("  OLD   STEW  ")}"))
            .Should().Equal("2026-10-01");
        Dates(await GetHistoryAsync(client, "?month=2026-10&recipe=Missing%20Dish")).Should().BeEmpty();
    }

    // ---- Month selection and paging -------------------------------------

    [Fact]
    public async Task Month_selection_pages_one_month_per_step_inside_the_range()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2025, 12, 25), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 3, 10), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 10, 20), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();

        // The default page is the newest month in the range that has an entry.
        var newest = await GetHistoryAsync(client);
        newest.GetProperty("month").GetProperty("key").GetString().Should().Be("2026-10");
        // The cursors walk the range's months, whether or not they have entries.
        newest.GetProperty("paging").GetProperty("newer").GetString().Should().Be("2026-11");
        newest.GetProperty("paging").GetProperty("older").GetString().Should().Be("2026-09");
        Dates(newest).Should().Equal("2026-10-20");

        var middle = await GetHistoryAsync(client, "?month=2026-03");
        middle.GetProperty("month").GetProperty("label").GetString().Should().Be("March 2026");
        middle.GetProperty("month").GetProperty("start").GetString().Should().Be("2026-03-01");
        middle.GetProperty("month").GetProperty("end").GetString().Should().Be("2026-03-31");
        middle.GetProperty("paging").GetProperty("newer").GetString().Should().Be("2026-04");
        middle.GetProperty("paging").GetProperty("older").GetString().Should().Be("2026-02");
        Dates(middle).Should().Equal("2026-03-10");

        // The oldest month of the applied range has no older cursor.
        var oldest = await GetHistoryAsync(client, "?month=2025-12");
        oldest.GetProperty("paging").GetProperty("older").ValueKind.Should().Be(JsonValueKind.Null);
        oldest.GetProperty("paging").GetProperty("newer").GetString().Should().Be("2026-01");
        Dates(oldest).Should().Equal("2025-12-25");
    }

    [Fact]
    public async Task Walking_the_month_cursors_visits_every_month_in_the_range_exactly_once()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2025, 12, 25), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 3, 10), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 10, 20), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();

        // The default range is 2025-12..2026-11 (twelve household months).
        var expected = new[]
        {
            "2025-12", "2026-01", "2026-02", "2026-03", "2026-04", "2026-05",
            "2026-06", "2026-07", "2026-08", "2026-09", "2026-10", "2026-11",
        };

        // Walk older from the newest month in the range.
        var olderWalk = new List<string>();
        var page = await GetHistoryAsync(client, "?month=2026-11");
        page.GetProperty("paging").GetProperty("newer").ValueKind.Should().Be(JsonValueKind.Null);
        olderWalk.Add(MonthKey(page));
        while (page.GetProperty("paging").GetProperty("older").GetString() is { } older)
        {
            page = await GetHistoryAsync(client, $"?month={older}");
            MonthKey(page).Should().Be(older);
            page.GetProperty("month").GetProperty("start").GetString().Should().Be($"{older}-01");
            olderWalk.Add(MonthKey(page));
        }

        olderWalk.Should().Equal(expected.Reverse());
        olderWalk.Should().OnlyHaveUniqueItems();

        // A month without entries still pages (a page is a month, not a month with
        // meals); its day list is empty rather than skipped.
        Days(await GetHistoryAsync(client, "?month=2026-01")).Should().BeEmpty();

        // Walk newer back to the newest month.
        var newerWalk = new List<string>();
        page = await GetHistoryAsync(client, "?month=2025-12");
        page.GetProperty("paging").GetProperty("older").ValueKind.Should().Be(JsonValueKind.Null);
        newerWalk.Add(MonthKey(page));
        while (page.GetProperty("paging").GetProperty("newer").GetString() is { } newer)
        {
            page = await GetHistoryAsync(client, $"?month={newer}");
            MonthKey(page).Should().Be(newer);
            newerWalk.Add(MonthKey(page));
        }

        newerWalk.Should().Equal(expected);
        newerWalk.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_month_page_keeps_days_as_a_sibling_of_month()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 10, 15), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();
        var root = await GetHistoryAsync(client, "?month=2026-10");

        // The shipped wire shape (docs/API.md §14, M11_PLAN §1.26): `days` is a
        // sibling of `month`, never nested inside it.
        root.GetProperty("month").TryGetProperty("days", out _).Should().BeFalse();
        root.TryGetProperty("days", out var days).Should().BeTrue();
        days.ValueKind.Should().Be(JsonValueKind.Array);
        days.GetArrayLength().Should().Be(1);

        var day = days[0];
        day.GetProperty("date").GetString().Should().Be("2026-10-15");
        day.GetProperty("entry_type").GetString().Should().Be("recipe");
        day.GetProperty("recipe_id").GetInt64().Should().Be(recipe.Id);
        day.GetProperty("name").GetString().Should().Be("Chili");
        day.TryGetProperty("protein", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_malformed_or_out_of_range_month_is_rejected()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?month=2026-13", Ct),
            "month");
        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?month=November", Ct),
            "month");
        // Beyond the default range's newest month (2026-11)...
        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?month=2026-12", Ct),
            "month");
        // ...and before its oldest month (2025-11).
        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?month=2025-10", Ct),
            "month");

        // A month inside an explicit range that reaches further back than the
        // default is accepted: the bound is the applied range, not the default.
        (await client.GetAsync($"{HistoryPath}?start=2025-06-01&end=2026-01-31&month=2025-06", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Range bound and malformed input ---------------------------------

    [Fact]
    public async Task The_range_is_capped_at_366_days_and_malformed_ranges_are_rejected()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        // Exactly 366 days inclusive is inside the cap.
        (await client.GetAsync($"{HistoryPath}?start=2025-01-01&end=2026-01-01", Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // One day more is not, and it is the shared VALIDATION_FAILED code.
        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?start=2025-01-01&end=2026-01-02", Ct),
            "range");

        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?start=2026-01-10&end=2026-01-01", Ct),
            "end");

        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?start=2026-02-30", Ct),
            "start");

        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?end=tomorrow", Ct),
            "end");
    }

    // ---- Filters ---------------------------------------------------------

    [Fact]
    public async Task The_entry_type_filter_is_and_combined_with_the_others()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 10, 1), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(
            new DateOnly(2026, 10, 2),
            CalendarEntryType.EatOut,
            recipeId: null,
            includeGroceries: false,
            snapshotDisplayText: "Sushi place");
        await SeedEntryAsync(new DateOnly(2026, 10, 3), CalendarEntryType.Holiday, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();

        Dates(await GetHistoryAsync(client, "?month=2026-10"))
            .Should().Equal("2026-10-03", "2026-10-02", "2026-10-01");
        Dates(await GetHistoryAsync(client, "?month=2026-10&entry_type=eat_out"))
            .Should().Equal("2026-10-02");
        // AND-combined: the eat-out day carries no matching recipe.
        Dates(await GetHistoryAsync(client, $"?month=2026-10&entry_type=eat_out&recipe={recipe.Id}"))
            .Should().BeEmpty();

        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?entry_type=brunch", Ct),
            "entry_type");
    }

    [Fact]
    public async Task The_recipe_and_protein_filters_reject_malformed_values()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?recipe=", Ct),
            "recipe");
        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?recipe=0", Ct),
            "recipe");
        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?protein=chicken", Ct),
            "protein");
        await AssertValidationFailedAsync(
            await client.GetAsync($"{HistoryPath}?protein=0", Ct),
            "protein");
    }

    [Fact]
    public async Task The_protein_filter_matches_the_rows_primary_protein()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var chicken = await SeedProteinAsync("Chicken");
        var beef = await SeedProteinAsync("Beef");
        var (primary, secondary) = chicken.Id < beef.Id ? (chicken, beef) : (beef, chicken);
        var meatloaf = await SeedRecipeAsync("Meatloaf");
        await SeedRecipeProteinAsync(meatloaf.Id, chicken.Id);
        await SeedRecipeProteinAsync(meatloaf.Id, beef.Id);
        var toast = await SeedRecipeAsync("Toast");
        await SeedEntryAsync(new DateOnly(2026, 10, 1), CalendarEntryType.Recipe, meatloaf.Id, true, "Meatloaf");
        await SeedEntryAsync(new DateOnly(2026, 10, 2), CalendarEntryType.Recipe, toast.Id, true, "Toast");

        using var client = factory.CreateClient();

        Dates(await GetHistoryAsync(client, "?month=2026-10"))
            .Should().Equal("2026-10-02", "2026-10-01");
        // The primary protein is the lowest protein id; the other listed protein
        // is not the row's primary, and a recipe with no protein row is excluded.
        Dates(await GetHistoryAsync(client, $"?month=2026-10&protein={primary.Id}"))
            .Should().Equal("2026-10-01");
        Dates(await GetHistoryAsync(client, $"?month=2026-10&protein={secondary.Id}"))
            .Should().BeEmpty();
        Dates(await GetHistoryAsync(client, $"?month=2026-10&protein={long.MaxValue}"))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_returns_an_empty_page_for_the_default_month()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 10, 20), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();
        var root = await GetHistoryAsync(client, "?entry_type=vacation");

        // The default month is the newest month in the range with an entry; the
        // filters narrow the days, not the page the caller lands on (§1.26).
        root.GetProperty("month").GetProperty("key").GetString().Should().Be("2026-10");
        root.GetProperty("paging").GetProperty("older").GetString().Should().Be("2026-09");
        Dates(root).Should().BeEmpty();
    }

    [Fact]
    public async Task An_explicit_range_bounds_the_days_and_the_month_cursors()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 3, 5), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 3, 12), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 4, 2), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();
        var root = await GetHistoryAsync(client, "?start=2026-03-10&end=2026-03-20");

        root.GetProperty("range").GetProperty("start").GetString().Should().Be("2026-03-10");
        root.GetProperty("range").GetProperty("end").GetString().Should().Be("2026-03-20");
        root.GetProperty("month").GetProperty("key").GetString().Should().Be("2026-03");
        // The month block reports the full calendar month, the days the range.
        root.GetProperty("month").GetProperty("start").GetString().Should().Be("2026-03-01");
        root.GetProperty("month").GetProperty("end").GetString().Should().Be("2026-03-31");
        Dates(root).Should().Equal("2026-03-12");
        root.GetProperty("paging").GetProperty("newer").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("paging").GetProperty("older").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- Permissions -----------------------------------------------------

    [Fact]
    public async Task Permission_matrix_gates_history_by_calendar_view()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var planner = await SeedUserAsync("planner", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(planner.Id, PermissionKeys.CalendarView);
        var cook = await SeedUserAsync("cook", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(cook.Id, PermissionKeys.RecipesView);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var session = await SignInAsync(factory, "planner", MemberPassword))
        {
            (await session.Client.GetAsync(HistoryPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            // calendar.view does not open the recipes.view statistics reads.
            (await session.Client.GetAsync(OverviewPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden);
        }

        using (var session = await SignInAsync(factory, "cook", MemberPassword))
        {
            var response = await session.Client.GetAsync(HistoryPath, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ErrorCodeAsync(response)).Should().Be("FORBIDDEN");
            (await session.Client.GetAsync(OverviewPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var session = await SignInAsync(factory, "root", AdminPassword))
        {
            (await session.Client.GetAsync(HistoryPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(HistoryPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Single_user_system_actor_reads_history_without_a_session()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        (await client.GetAsync(HistoryPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- Helpers ----------------------------------------------------------

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private async Task<ApiFactory> CreateSingleUserFactoryAsync(TestTimeProvider clock)
    {
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
        return CreateFactory(clock);
    }

    private static async Task<JsonElement> GetHistoryAsync(HttpClient client, string? query = null)
    {
        var response = await client.GetAsync($"{HistoryPath}{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static List<JsonElement> Days(JsonElement root) =>
        root.GetProperty("days").EnumerateArray().ToList();

    private static List<string?> Dates(JsonElement root) =>
        Days(root).Select(day => day.GetProperty("date").GetString()).ToList();

    private static string MonthKey(JsonElement root) =>
        root.GetProperty("month").GetProperty("key").GetString()!;

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var root = await ReadJsonAsync(response);
        return root.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task AssertValidationFailedAsync(
        HttpResponseMessage response,
        string field)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var root = await ReadJsonAsync(response);
        root.GetProperty("error").GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        var fieldErrors = root.GetProperty("error").GetProperty("details").GetProperty("field_errors");
        fieldErrors.TryGetProperty(field, out var messages)
            .Should().BeTrue($"field_errors should carry '{field}'");
        messages.GetArrayLength().Should().BeGreaterThan(0);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password)
    {
        var cookies = new CookieContainerHandler(new System.Net.CookieContainer());
        var client = factory.CreateDefaultClient(cookies);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);

        var login = await client.SendAsync(request, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return new SignedInClient(
            client,
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
    }

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M11 Meal History Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task MarkSetupCompletedAsync()
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            Ct);
    }

    private async Task<User> SeedUserAsync(string username, string password, UserRole role)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private async Task SeedPermissionAsync(long userId, string key)
    {
        await using var context = fixture.CreateContext();
        context.UserPermissions.Add(new UserPermission { UserId = userId, PermissionKey = key });
        await context.SaveChangesAsync(Ct);
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

    private async Task RenameRecipeAsync(long recipeId, string name)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = await context.Recipes.SingleAsync(candidate => candidate.Id == recipeId, Ct);
        recipe.Name = normalized.Display;
        recipe.NormalizedName = normalized.Normalized;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<Protein> SeedProteinAsync(string name)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var existing = await context.Proteins
            .FirstOrDefaultAsync(protein => protein.NormalizedName == normalized.Normalized, Ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new Protein
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            IsActive = true,
        };
        context.Proteins.Add(created);
        await context.SaveChangesAsync(Ct);
        return created;
    }

    private async Task SeedRecipeProteinAsync(long recipeId, long proteinId)
    {
        await using var context = fixture.CreateContext();
        context.RecipeProteins.Add(new RecipeProtein { RecipeId = recipeId, ProteinId = proteinId });
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedEntryAsync(
        DateOnly date,
        CalendarEntryType entryType,
        long? recipeId,
        bool includeGroceries,
        string? snapshotRecipeName = null,
        string? snapshotDisplayText = null)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = entryType,
            RecipeId = recipeId,
            IncludeGroceries = includeGroceries,
            SnapshotRecipeName = snapshotRecipeName,
            SnapshotDisplayText = snapshotDisplayText ?? snapshotRecipeName ?? entryType.ToString(),
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
