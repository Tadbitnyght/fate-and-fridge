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
/// HTTP-level verification of the M11-01 statistics overview and the M11-02
/// per-recipe metrics (docs/M11_PLAN.md §1.3, §1.4, §1.12, §1.13, §1.19, §1.20,
/// §1.21, §1.24, §1.27, §2.2): household-local windows, the strictly-past eaten
/// rule, retired/deleted snapshot integrity, the two rankings with their
/// deterministic ordering, the month/year counts, the primary-protein
/// distribution, the special-event counts, the three per-recipe metrics on the
/// endpoint / recipe list / recipe detail, the history-aware list sorts, and the
/// <c>recipes.view</c> permission matrix. The clock is injected so "today" is
/// deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M11StatisticsCollection.Name)]
public sealed class M11StatisticsIntegrationTests(M11StatisticsFixture fixture) : IAsyncLifetime
{
    private const string OverviewPath = "/api/v1/statistics/overview";
    private const string RecipeMetricsPath = "/api/v1/statistics/recipes";
    private const string HistoryPath = "/api/v1/statistics/history";
    private const string RecipesPath = "/api/v1/recipes";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "statistics-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC on a Sunday, so America/Chicago is the same calendar date and
    // comfortably after the real host date so an injected clock never expires a
    // session cookie. The month/year boundary tests straddle October 2026 /
    // December 2025.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Windows and the empty household --------------------------------

    [Fact]
    public async Task Empty_household_reports_household_local_windows_and_zeroed_metrics()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var root = await GetOverviewAsync(client);

        var windows = root.GetProperty("windows");
        windows.GetProperty("today").GetString().Should().Be("2026-11-15");
        windows.GetProperty("timezone").GetString().Should().Be("America/Chicago");
        windows.GetProperty("month").GetProperty("label").GetString().Should().Be("November 2026");
        windows.GetProperty("month").GetProperty("start").GetString().Should().Be("2026-11-01");
        windows.GetProperty("month").GetProperty("end").GetString().Should().Be("2026-11-30");
        windows.GetProperty("year").GetProperty("label").GetString().Should().Be("2026");
        windows.GetProperty("year").GetProperty("start").GetString().Should().Be("2026-01-01");
        windows.GetProperty("year").GetProperty("end").GetString().Should().Be("2026-12-31");

        root.GetProperty("most_eaten").GetArrayLength().Should().Be(0);
        root.GetProperty("least_recently_eaten").GetArrayLength().Should().Be(0);
        root.GetProperty("never_eaten").GetInt32().Should().Be(0);
        root.GetProperty("meal_count_month").GetInt32().Should().Be(0);
        root.GetProperty("meal_count_year").GetInt32().Should().Be(0);
        root.GetProperty("protein_distribution").GetProperty("proteins").GetArrayLength().Should().Be(0);
        root.GetProperty("protein_distribution").GetProperty("no_primary_protein").GetInt32().Should().Be(0);

        var events = EventCounts(root);
        events.Should().HaveCount(7);
        events.Select(entry => entry.GetProperty("entry_type").GetString()).Should().Equal(
            "eat_out",
            "host_family_dinner",
            "go_to_family_dinner",
            "holiday",
            "misc",
            "fend_for_yourself",
            "vacation");
        events.Should().OnlyContain(entry => entry.GetProperty("count").GetInt32() == 0);
    }

    // ---- Eaten is strictly past ------------------------------------------

    [Fact]
    public async Task Eaten_is_strictly_past_and_today_is_not_eaten()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 14), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 16), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var top = MostEaten(root).Single();
        top.GetProperty("recipe_id").GetInt64().Should().Be(recipe.Id);
        top.GetProperty("name").GetString().Should().Be("Chili");
        top.GetProperty("status").GetString().Should().Be("normal");
        top.GetProperty("times_eaten").GetInt32().Should().Be(1);
        top.GetProperty("last_eaten").GetString().Should().Be("2026-11-14");

        root.GetProperty("never_eaten").GetInt32().Should().Be(0);

        // The count is every entry type in the window, including today's dinner.
        root.GetProperty("meal_count_month").GetInt32().Should().Be(3);
        root.GetProperty("meal_count_year").GetInt32().Should().Be(3);
    }

    // ---- Occurrence eligibility ------------------------------------------

    [Fact]
    public async Task Recipe_carrying_specials_count_and_eat_out_fend_and_go_to_do_not()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Pot Roast");
        await SeedEntryAsync(new DateOnly(2026, 3, 10), CalendarEntryType.HostFamilyDinner, recipe.Id, true, "Pot Roast");
        await SeedEntryAsync(new DateOnly(2026, 3, 11), CalendarEntryType.Holiday, recipe.Id, true, "Pot Roast");
        await SeedEntryAsync(new DateOnly(2026, 3, 12), CalendarEntryType.Misc, recipe.Id, true, "Pot Roast");
        await SeedEntryAsync(new DateOnly(2026, 3, 13), CalendarEntryType.EatOut, null, false);
        await SeedEntryAsync(new DateOnly(2026, 3, 14), CalendarEntryType.FendForYourself, null, false);

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        // Only the three recipe-carrying specials count toward the recipe metrics.
        MostEaten(root).Single().GetProperty("times_eaten").GetInt32().Should().Be(3);
        root.GetProperty("protein_distribution").GetProperty("no_primary_protein").GetInt32().Should().Be(3);

        // They all still appear in the special-event counts.
        EventCount(root, "host_family_dinner").Should().Be(1);
        EventCount(root, "holiday").Should().Be(1);
        EventCount(root, "misc").Should().Be(1);
        EventCount(root, "eat_out").Should().Be(1);
        EventCount(root, "fend_for_yourself").Should().Be(1);
    }

    // ---- Historical integrity --------------------------------------------

    [Fact]
    public async Task A_retired_recipe_keeps_its_meals_and_carries_its_badge()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Old Stew");
        await SeedEntryAsync(new DateOnly(2026, 2, 1), CalendarEntryType.Recipe, recipe.Id, true, "Old Stew");
        await SeedEntryAsync(new DateOnly(2026, 3, 1), CalendarEntryType.Recipe, recipe.Id, true, "Old Stew");
        await SetRecipeStatusAsync(recipe.Id, RecipeStatus.Retired);

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var top = MostEaten(root).Single();
        top.GetProperty("recipe_id").GetInt64().Should().Be(recipe.Id);
        top.GetProperty("status").GetString().Should().Be("retired");
        top.GetProperty("times_eaten").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_snapshot_only_occurrence_counts_as_a_deleted_tombstone()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        await SeedEntryAsync(
            new DateOnly(2026, 3, 1),
            CalendarEntryType.Recipe,
            recipeId: null,
            includeGroceries: true,
            snapshotRecipeName: "Grandma's Stew");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var top = MostEaten(root).Single();
        top.GetProperty("recipe_id").ValueKind.Should().Be(JsonValueKind.Null);
        top.GetProperty("name").GetString().Should().Be("Grandma's Stew");
        top.GetProperty("status").GetString().Should().Be("deleted");
        top.GetProperty("times_eaten").GetInt32().Should().Be(1);
        top.GetProperty("last_eaten").GetString().Should().Be("2026-03-01");

        // A tombstone is not a live recipe, so it never adds to never_eaten.
        root.GetProperty("never_eaten").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Meals_group_by_recipe_id_or_normalized_snapshot_name()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(new DateOnly(2026, 2, 1), CalendarEntryType.Recipe, recipe.Id, true, "Tacos");
        await SeedEntryAsync(new DateOnly(2026, 2, 10), CalendarEntryType.Recipe, recipe.Id, true, "Tacos");
        await RenameRecipeAsync(recipe.Id, "Taco Night");

        await SeedEntryAsync(new DateOnly(2026, 2, 11), CalendarEntryType.Recipe, null, true, "Grandma's Stew");
        await SeedEntryAsync(new DateOnly(2026, 2, 12), CalendarEntryType.Recipe, null, true, "grandma's   stew");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var rows = MostEaten(root);
        rows.Should().HaveCount(2);

        // A renamed recipe is one identity (recipe_id wins), so its history is
        // never double-counted and the live name is shown.
        var renamed = rows.Single(row => row.GetProperty("recipe_id").ValueKind == JsonValueKind.Number);
        renamed.GetProperty("name").GetString().Should().Be("Taco Night");
        renamed.GetProperty("times_eaten").GetInt32().Should().Be(2);

        // Two snapshot spellings collapse to one normalized-name identity, and
        // the most recent snapshot is shown.
        var tombstone = rows.Single(row => row.GetProperty("recipe_id").ValueKind == JsonValueKind.Null);
        tombstone.GetProperty("name").GetString().Should().Be("grandma's stew");
        tombstone.GetProperty("times_eaten").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_live_recipe_and_a_same_named_tombstone_stay_distinct()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Stew");
        await SeedEntryAsync(new DateOnly(2026, 3, 1), CalendarEntryType.Recipe, recipe.Id, true, "Stew");
        await SeedEntryAsync(new DateOnly(2026, 3, 2), CalendarEntryType.Recipe, null, true, "Stew");

        using var client = factory.CreateClient();
        var rows = MostEaten(await GetOverviewAsync(client));

        rows.Should().HaveCount(2);
        rows.Should().ContainSingle(row => row.GetProperty("recipe_id").ValueKind == JsonValueKind.Number);
        rows.Should().ContainSingle(row => row.GetProperty("recipe_id").ValueKind == JsonValueKind.Null);
    }

    // ---- Rankings ---------------------------------------------------------

    [Fact]
    public async Task Most_eaten_returns_the_top_ten_with_a_deterministic_name_tie_break()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        for (var index = 0; index < 12; index++)
        {
            var recipe = await SeedRecipeAsync($"Recipe {index:00}");
            await SeedEntryAsync(
                new DateOnly(2026, 1, 1).AddDays(index),
                CalendarEntryType.Recipe,
                recipe.Id,
                true,
                recipe.Name);
        }

        using var client = factory.CreateClient();
        var rows = MostEaten(await GetOverviewAsync(client));

        rows.Should().HaveCount(10);
        rows.Select(row => row.GetProperty("name").GetString()).Should().Equal(
            "Recipe 00", "Recipe 01", "Recipe 02", "Recipe 03", "Recipe 04",
            "Recipe 05", "Recipe 06", "Recipe 07", "Recipe 08", "Recipe 09");
    }

    [Fact]
    public async Task Least_recently_eaten_is_oldest_first_and_excludes_never_eaten()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var older = await SeedRecipeAsync("Older");
        var newer = await SeedRecipeAsync("Newer");
        var never = await SeedRecipeAsync("Never Eaten");
        await SeedEntryAsync(new DateOnly(2026, 1, 5), CalendarEntryType.Recipe, older.Id, true, "Older");
        await SeedEntryAsync(new DateOnly(2026, 3, 10), CalendarEntryType.Recipe, newer.Id, true, "Newer");
        await SeedEntryAsync(new DateOnly(2026, 11, 20), CalendarEntryType.Recipe, never.Id, true, "Never Eaten");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var leastRecent = LeastRecentlyEaten(root);
        leastRecent.Should().HaveCount(2);
        leastRecent[0].GetProperty("name").GetString().Should().Be("Older");
        leastRecent[0].GetProperty("last_eaten").GetString().Should().Be("2026-01-05");
        leastRecent[1].GetProperty("name").GetString().Should().Be("Newer");

        MostEaten(root).Select(row => row.GetProperty("name").GetString())
            .Should().BeEquivalentTo(["Older", "Newer"]);
        root.GetProperty("never_eaten").GetInt32().Should().Be(1);
    }

    // ---- Meal and event counts -------------------------------------------

    [Fact]
    public async Task Meal_counts_use_the_household_month_and_year_and_count_every_type()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("November Meal");
        await SeedEntryAsync(new DateOnly(2026, 1, 4), CalendarEntryType.Holiday, null, false);
        await SeedEntryAsync(new DateOnly(2026, 2, 28), CalendarEntryType.EatOut, null, false);
        await SeedEntryAsync(new DateOnly(2026, 11, 1), CalendarEntryType.Recipe, recipe.Id, true, "November Meal");
        await SeedEntryAsync(Today, CalendarEntryType.Vacation, null, false);
        await SeedEntryAsync(new DateOnly(2025, 12, 31), CalendarEntryType.Holiday, null, false);

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        root.GetProperty("meal_count_month").GetInt32().Should().Be(2);
        root.GetProperty("meal_count_year").GetInt32().Should().Be(4);

        EventCount(root, "holiday").Should().Be(1);
        EventCount(root, "eat_out").Should().Be(1);
        EventCount(root, "vacation").Should().Be(1);
    }

    [Fact]
    public async Task Meal_count_month_is_bounded_to_the_household_calendar_month()
    {
        // Regression (#148): the month count used to run from the first of the
        // month through December 31, so every later-in-the-year entry inflated
        // it. Household today is 2026-10-16, so the month window is
        // [2026-10-01, 2026-10-31] and the year window is [2026-01-01, 2026-12-31].
        var now = new DateTimeOffset(2026, 10, 16, 18, 0, 0, TimeSpan.Zero);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(now));
        var recipe = await SeedRecipeAsync("October Meal");
        await SeedEntryAsync(new DateOnly(2026, 10, 5), CalendarEntryType.Recipe, recipe.Id, true, "October Meal");
        await SeedEntryAsync(new DateOnly(2026, 10, 20), CalendarEntryType.Recipe, recipe.Id, true, "October Meal");
        await SeedEntryAsync(new DateOnly(2026, 11, 5), CalendarEntryType.Recipe, recipe.Id, true, "October Meal");
        await SeedEntryAsync(new DateOnly(2026, 12, 31), CalendarEntryType.Holiday, null, false);
        await SeedEntryAsync(new DateOnly(2025, 12, 31), CalendarEntryType.Holiday, null, false);

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        // A future day within the month still counts (§1.24); next month, the
        // rest of this year, and the previous year do not.
        root.GetProperty("meal_count_month").GetInt32().Should().Be(2);
        root.GetProperty("meal_count_year").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Meal_counts_are_bounded_at_the_year_boundary()
    {
        // Household today 2026-01-15: the previous December falls outside both
        // the month and the year windows.
        var now = new DateTimeOffset(2026, 1, 15, 18, 0, 0, TimeSpan.Zero);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(now));
        var recipe = await SeedRecipeAsync("January Meal");
        await SeedEntryAsync(new DateOnly(2025, 12, 31), CalendarEntryType.Holiday, null, false);
        await SeedEntryAsync(new DateOnly(2026, 1, 2), CalendarEntryType.Recipe, recipe.Id, true, "January Meal");
        await SeedEntryAsync(new DateOnly(2026, 1, 20), CalendarEntryType.Recipe, recipe.Id, true, "January Meal");
        await SeedEntryAsync(new DateOnly(2026, 2, 3), CalendarEntryType.Recipe, recipe.Id, true, "January Meal");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        root.GetProperty("meal_count_month").GetInt32().Should().Be(2);
        root.GetProperty("meal_count_year").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Special_event_counts_cover_all_seven_types_over_the_calendar_year()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        await SeedEntryAsync(new DateOnly(2026, 1, 1), CalendarEntryType.EatOut, null, false);
        await SeedEntryAsync(new DateOnly(2026, 1, 2), CalendarEntryType.HostFamilyDinner, null, false);
        await SeedEntryAsync(new DateOnly(2026, 1, 3), CalendarEntryType.GoToFamilyDinner, null, false);
        await SeedEntryAsync(new DateOnly(2026, 1, 4), CalendarEntryType.Holiday, null, false);
        await SeedEntryAsync(new DateOnly(2026, 1, 5), CalendarEntryType.Misc, null, false);
        await SeedEntryAsync(new DateOnly(2026, 1, 6), CalendarEntryType.FendForYourself, null, false);
        await SeedEntryAsync(new DateOnly(2026, 1, 7), CalendarEntryType.Vacation, null, false);
        // Previous year: outside the household calendar year.
        await SeedEntryAsync(new DateOnly(2025, 12, 31), CalendarEntryType.Holiday, null, false);

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        EventCounts(root).Select(entry => entry.GetProperty("count").GetInt32())
            .Should().Equal(1, 1, 1, 1, 1, 1, 1);
    }

    // ---- Protein distribution --------------------------------------------

    [Fact]
    public async Task Protein_distribution_counts_recipe_occurrences_and_reports_no_primary_protein()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var chicken = await SeedProteinAsync("Chicken");
        var beef = await SeedProteinAsync("Beef");
        var pork = await SeedProteinAsync("Pork");

        var chickenRecipe = await SeedRecipeAsync("Chicken Dinner");
        await SeedRecipeProteinAsync(chickenRecipe.Id, chicken.Id);
        var mixedRecipe = await SeedRecipeAsync("Pork and Beef");
        await SeedRecipeProteinAsync(mixedRecipe.Id, beef.Id);
        await SeedRecipeProteinAsync(mixedRecipe.Id, pork.Id);
        var noProteinRecipe = await SeedRecipeAsync("Mystery Casserole");

        await SeedEntryAsync(new DateOnly(2026, 3, 1), CalendarEntryType.Recipe, chickenRecipe.Id, true, "Chicken Dinner");
        await SeedEntryAsync(new DateOnly(2026, 3, 2), CalendarEntryType.Recipe, chickenRecipe.Id, true, "Chicken Dinner");
        await SeedEntryAsync(new DateOnly(2026, 3, 3), CalendarEntryType.Recipe, mixedRecipe.Id, true, "Pork and Beef");
        await SeedEntryAsync(new DateOnly(2026, 3, 4), CalendarEntryType.Recipe, noProteinRecipe.Id, true, "Mystery Casserole");
        // A tombstone has no live protein rows, so it is reported explicitly.
        await SeedEntryAsync(new DateOnly(2026, 3, 5), CalendarEntryType.Recipe, null, true, "Ghost Stew");
        // A no-recipe day never enters the distribution.
        await SeedEntryAsync(new DateOnly(2026, 3, 6), CalendarEntryType.EatOut, null, false);

        using var client = factory.CreateClient();
        var distribution = (await GetOverviewAsync(client)).GetProperty("protein_distribution");

        var bars = distribution.GetProperty("proteins").EnumerateArray().ToList();
        bars.Select(bar => (bar.GetProperty("name").GetString(), bar.GetProperty("count").GetInt32()))
            .Should().Equal(
                ("Chicken", 2),
                ("Beef", 1),
                ("Pork", 1));
        bars[0].GetProperty("protein_id").GetInt64().Should().Be(chicken.Id);

        distribution.GetProperty("no_primary_protein").GetInt32().Should().Be(2);
    }

    // ---- M11-02 per-recipe metrics ---------------------------------------

    [Fact]
    public async Task Recipe_metrics_endpoint_splits_eaten_and_planned()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var chili = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 9, 1), CalendarEntryType.HostFamilyDinner, chili.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 10), CalendarEntryType.Recipe, chili.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 14), CalendarEntryType.Recipe, chili.Id, true, "Chili");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, chili.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 20), CalendarEntryType.Recipe, chili.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 16), CalendarEntryType.EatOut, null, false);

        using var client = factory.CreateClient();
        var metrics = await GetRecipeMetricsAsync(client, chili.Id);

        // The recipe-carrying special and the two past recipe days are eaten;
        // today's dinner is Next Planned, not Times Eaten (strictly past).
        metrics.GetProperty("last_eaten").GetString().Should().Be("2026-11-14");
        metrics.GetProperty("next_planned").GetString().Should().Be("2026-11-15");
        metrics.GetProperty("times_eaten").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Recipe_metrics_endpoint_reports_no_history_as_null_not_a_fake_zero()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var fresh = await SeedRecipeAsync("Fresh Recipe");
        var futureOnly = await SeedRecipeAsync("Future Only");
        await SeedEntryAsync(new DateOnly(2026, 12, 1), CalendarEntryType.Recipe, futureOnly.Id, true, "Future Only");

        using var client = factory.CreateClient();

        var freshMetrics = await GetRecipeMetricsAsync(client, fresh.Id);
        freshMetrics.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        freshMetrics.GetProperty("next_planned").ValueKind.Should().Be(JsonValueKind.Null);
        freshMetrics.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);

        // A planned-but-never-eaten recipe has Next Planned but still no Times
        // Eaten count.
        var futureMetrics = await GetRecipeMetricsAsync(client, futureOnly.Id);
        futureMetrics.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        futureMetrics.GetProperty("next_planned").GetString().Should().Be("2026-12-01");
        futureMetrics.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Recipe_metrics_endpoint_is_not_found_for_unknown_and_hard_deleted_recipes()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var ghost = await SeedRecipeAsync("Ghost Stew");
        await SeedEntryAsync(new DateOnly(2026, 5, 1), CalendarEntryType.Recipe, ghost.Id, true, "Ghost Stew");

        // Simulates M12's hard delete: the recipe row is gone and the entry falls
        // back to its stored snapshot name (FK ON DELETE SET NULL).
        await DeleteRecipeAsync(ghost.Id);

        using var client = factory.CreateClient();

        var unknown = await client.GetAsync($"{RecipeMetricsPath}/999999", Ct);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorEnvelopeAsync(unknown)).Should().Be("NOT_FOUND");

        var deleted = await client.GetAsync($"{RecipeMetricsPath}/{ghost.Id}", Ct);
        deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorEnvelopeAsync(deleted)).Should().Be("NOT_FOUND");

        // The tombstone's meals still count in the household overview.
        var overview = await GetOverviewAsync(client);
        var tombstone = MostEaten(overview).Single();
        tombstone.GetProperty("recipe_id").ValueKind.Should().Be(JsonValueKind.Null);
        tombstone.GetProperty("name").GetString().Should().Be("Ghost Stew");
        tombstone.GetProperty("status").GetString().Should().Be("deleted");
        tombstone.GetProperty("times_eaten").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Recipes_list_carries_the_metrics_for_every_item_without_a_per_card_fetch()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var eaten = await SeedRecipeAsync("Eaten Recipe");
        var planned = await SeedRecipeAsync("Planned Recipe");
        await SeedRecipeAsync("Fresh Recipe");
        await SeedEntryAsync(new DateOnly(2026, 11, 10), CalendarEntryType.Recipe, eaten.Id, true, "Eaten Recipe");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, eaten.Id, true, "Eaten Recipe");
        await SeedEntryAsync(new DateOnly(2026, 11, 25), CalendarEntryType.Recipe, planned.Id, true, "Planned Recipe");

        using var client = factory.CreateClient();
        var items = await ListItemsAsync(client, "?limit=100");

        var eatenItem = items.Single(item => item.GetProperty("name").GetString() == "Eaten Recipe");
        eatenItem.GetProperty("last_eaten").GetString().Should().Be("2026-11-10");
        eatenItem.GetProperty("next_planned").GetString().Should().Be("2026-11-15");
        eatenItem.GetProperty("times_eaten").GetInt32().Should().Be(1);

        var plannedItem = items.Single(item => item.GetProperty("name").GetString() == "Planned Recipe");
        plannedItem.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        plannedItem.GetProperty("next_planned").GetString().Should().Be("2026-11-25");
        plannedItem.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);

        var freshItem = items.Single(item => item.GetProperty("name").GetString() == "Fresh Recipe");
        freshItem.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        freshItem.GetProperty("next_planned").ValueKind.Should().Be(JsonValueKind.Null);
        freshItem.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Recipes_list_history_sorts_use_history_and_break_ties_on_id()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var older = await SeedRecipeAsync("Older");
        var newer = await SeedRecipeAsync("Newer");
        var frequent = await SeedRecipeAsync("Frequent");
        await SeedRecipeAsync("Never");
        await SeedEntryAsync(new DateOnly(2026, 1, 5), CalendarEntryType.Recipe, older.Id, true, "Older");
        await SeedEntryAsync(new DateOnly(2026, 3, 10), CalendarEntryType.Recipe, newer.Id, true, "Newer");
        await SeedEntryAsync(new DateOnly(2026, 4, 1), CalendarEntryType.Recipe, frequent.Id, true, "Frequent");
        await SeedEntryAsync(new DateOnly(2026, 4, 2), CalendarEntryType.Recipe, frequent.Id, true, "Frequent");
        await SeedEntryAsync(new DateOnly(2026, 4, 3), CalendarEntryType.Recipe, frequent.Id, true, "Frequent");
        await SeedEntryAsync(new DateOnly(2026, 4, 4), CalendarEntryType.Recipe, frequent.Id, true, "Frequent");

        using var client = factory.CreateClient();

        (await ListNamesAsync(client, "?sort=recently_eaten"))
            .Should().Equal("Frequent", "Newer", "Older", "Never");
        (await ListNamesAsync(client, "?sort=least_recently_eaten"))
            .Should().Equal("Older", "Newer", "Frequent", "Never");
        // "Older" and "Newer" tie on a count of one and break on id (Older first).
        (await ListNamesAsync(client, "?sort=most_eaten"))
            .Should().Equal("Frequent", "Older", "Newer", "Never");
    }

    [Fact]
    public async Task Recipe_detail_document_carries_the_metrics()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var chili = await SeedRecipeAsync("Chili");
        var fresh = await SeedRecipeAsync("Fresh Recipe");
        await SeedEntryAsync(new DateOnly(2026, 11, 10), CalendarEntryType.Recipe, chili.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 20), CalendarEntryType.Recipe, chili.Id, true, "Chili");

        using var client = factory.CreateClient();

        var statistics = (await GetRecipeDetailAsync(client, chili.Id)).GetProperty("statistics");
        statistics.GetProperty("last_eaten").GetString().Should().Be("2026-11-10");
        statistics.GetProperty("next_planned").GetString().Should().Be("2026-11-20");
        statistics.GetProperty("times_eaten").GetInt32().Should().Be(1);

        var freshStatistics = (await GetRecipeDetailAsync(client, fresh.Id)).GetProperty("statistics");
        freshStatistics.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        freshStatistics.GetProperty("next_planned").ValueKind.Should().Be(JsonValueKind.Null);
        freshStatistics.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- Permissions -----------------------------------------------------

    [Fact]
    public async Task Permission_matrix_gates_the_overview()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var viewer = await SeedUserAsync("viewer", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(viewer.Id, PermissionKeys.RecipesView);
        await SeedUserAsync("nobody", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var viewerSession = await SignInAsync(factory, "viewer", MemberPassword))
        {
            (await viewerSession.Client.GetAsync(OverviewPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            (await deniedSession.Client.GetAsync(OverviewPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden);
        }

        using (var adminSession = await SignInAsync(factory, "root", AdminPassword))
        {
            (await adminSession.Client.GetAsync(OverviewPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(OverviewPath, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Permission_matrix_gates_the_per_recipe_metrics()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var viewer = await SeedUserAsync("viewer", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(viewer.Id, PermissionKeys.RecipesView);
        await SeedUserAsync("nobody", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        var recipe = await SeedRecipeAsync("Chili");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        var path = $"{RecipeMetricsPath}/{recipe.Id}";

        using (var viewerSession = await SignInAsync(factory, "viewer", MemberPassword))
        {
            (await viewerSession.Client.GetAsync(path, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            (await deniedSession.Client.GetAsync(path, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden);
        }

        using (var adminSession = await SignInAsync(factory, "root", AdminPassword))
        {
            (await adminSession.Client.GetAsync(path, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(path, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Edit_implies_view_serves_the_statistics_and_history_reads()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var recipeEditor = await SeedUserAsync("recipe-editor", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(recipeEditor.Id, PermissionKeys.RecipesEdit);
        var calendarEditor = await SeedUserAsync("calendar-editor", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(calendarEditor.Id, PermissionKeys.CalendarEdit);
        var grocer = await SeedUserAsync("grocer", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(grocer.Id, PermissionKeys.GroceriesView);
        await MarkSetupCompletedAsync();
        var recipe = await SeedRecipeAsync("Chili");
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var session = await SignInAsync(factory, "recipe-editor", MemberPassword))
        {
            // recipes.edit implies recipes.view (PermissionEvaluator.ForMember).
            (await session.Client.GetAsync(OverviewPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await session.Client.GetAsync($"{RecipeMetricsPath}/{recipe.Id}", Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
            (await session.Client.GetAsync(RecipesPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            // It does not imply calendar.view.
            (await session.Client.GetAsync(HistoryPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var session = await SignInAsync(factory, "calendar-editor", MemberPassword))
        {
            (await session.Client.GetAsync(HistoryPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            // calendar.edit implies only calendar.view, never recipes.view.
            (await session.Client.GetAsync(OverviewPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var session = await SignInAsync(factory, "grocer", MemberPassword))
        {
            (await session.Client.GetAsync(OverviewPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await session.Client.GetAsync(HistoryPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }

    [Fact]
    public async Task Recipes_list_is_gated_by_recipes_view()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var viewer = await SeedUserAsync("viewer", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(viewer.Id, PermissionKeys.RecipesView);
        await SeedUserAsync("nobody", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var session = await SignInAsync(factory, "viewer", MemberPassword))
        {
            (await session.Client.GetAsync(RecipesPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var session = await SignInAsync(factory, "nobody", MemberPassword))
        {
            (await session.Client.GetAsync(RecipesPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(RecipesPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Single_user_system_actor_reads_without_a_session()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        (await client.GetAsync(OverviewPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
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

    private static async Task<JsonElement> GetRecipeDetailAsync(HttpClient client, long recipeId)
    {
        var response = await client.GetAsync($"{RecipesPath}/{recipeId}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static async Task<List<JsonElement>> ListItemsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{RecipesPath}{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        return root.GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task<List<string?>> ListNamesAsync(HttpClient client, string query)
    {
        var items = await ListItemsAsync(client, query);
        return items.Select(item => item.GetProperty("name").GetString()).ToList();
    }

    private static async Task<string?> ErrorEnvelopeAsync(HttpResponseMessage response)
    {
        var root = await ReadJsonAsync(response);
        return root.GetProperty("error").GetProperty("code").GetString();
    }

    private static List<JsonElement> MostEaten(JsonElement root) =>
        root.GetProperty("most_eaten").EnumerateArray().ToList();

    private static List<JsonElement> LeastRecentlyEaten(JsonElement root) =>
        root.GetProperty("least_recently_eaten").EnumerateArray().ToList();

    private static List<JsonElement> EventCounts(JsonElement root) =>
        root.GetProperty("special_event_counts").EnumerateArray().ToList();

    private static int EventCount(JsonElement root, string entryType) =>
        EventCounts(root)
            .Single(entry => entry.GetProperty("entry_type").GetString() == entryType)
            .GetProperty("count")
            .GetInt32();

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
            HouseholdName = "M11 Statistics Household",
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

    private async Task DeleteRecipeAsync(long recipeId)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM recipes WHERE id = {recipeId}",
            Ct);
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

    private async Task SetRecipeStatusAsync(long recipeId, RecipeStatus status)
    {
        await using var context = fixture.CreateContext();
        var recipe = await context.Recipes.SingleAsync(candidate => candidate.Id == recipeId, Ct);
        recipe.Status = status;
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
            SnapshotDisplayText = snapshotRecipeName ?? entryType.ToString(),
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
