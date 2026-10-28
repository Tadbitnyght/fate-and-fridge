using System.Net;
using System.Text.Json;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Exhaustive definitional battery: the occurrence/eaten rules
/// historical integrity (retired, hard-deleted tombstone, renamed), the
/// household-local month/year windows with a non-UTC boundary that a UTC-naive
/// implementation would fail, the two rankings, the primary-protein
/// distribution, and the special-event counts. The existing per-issue suites
/// (StatisticsIntegrationTests, MealHistoryIntegrationTests
/// MealOccurrenceRulesTests, HouseholdDateLabelsTests) already cover their
/// slices; this class fills the gaps those left.
/// </summary>
[Trait("Category", "Integration")]
[Collection(StatisticsCollection.Name)]
public sealed class StatisticsBatteryIntegrationTests(StatisticsFixture fixture) : IAsyncLifetime
{
    private const string OverviewPath = "/api/v1/statistics/overview";
    private const string RecipeMetricsPath = "/api/v1/statistics/recipes";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC on a Sunday, so America/Chicago is the same calendar date.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Occurrence and eaten rules

    [Fact]
    public async Task Today_is_only_next_planned_and_is_never_eaten()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var chili = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, chili.Id, true, "Chili");

        using var client = factory.CreateClient();
        var metrics = await GetRecipeMetricsAsync(client, chili.Id);

        metrics.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        metrics.GetProperty("next_planned").GetString().Should().Be("2026-11-15");
        metrics.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);

        var root = await GetOverviewAsync(client);
        MostEaten(root).Should().BeEmpty();
        LeastRecentlyEaten(root).Should().BeEmpty();
        root.GetProperty("never_eaten").GetInt32().Should().Be(1);
        // Today's dinner is still a meal (every entry type counts)...
        root.GetProperty("meal_count_month").GetInt32().Should().Be(1);
        root.GetProperty("meal_count_year").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_past_meal_is_eaten_and_has_no_next_planned()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var chili = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 11, 10), CalendarEntryType.Recipe, chili.Id, true, "Chili");

        using var client = factory.CreateClient();
        var metrics = await GetRecipeMetricsAsync(client, chili.Id);

        metrics.GetProperty("last_eaten").GetString().Should().Be("2026-11-10");
        metrics.GetProperty("next_planned").ValueKind.Should().Be(JsonValueKind.Null);
        metrics.GetProperty("times_eaten").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Only_recipe_carrying_types_feed_recipe_metrics_but_every_type_is_a_meal()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Pot Roast");
        // The three recipe-carrying specials are occurrences...
        await SeedEntryAsync(new DateOnly(2026, 3, 1), CalendarEntryType.HostFamilyDinner, recipe.Id, true, "Pot Roast");
        await SeedEntryAsync(new DateOnly(2026, 3, 2), CalendarEntryType.Holiday, recipe.Id, true, "Pot Roast");
        await SeedEntryAsync(new DateOnly(2026, 3, 3), CalendarEntryType.Misc, recipe.Id, true, "Pot Roast");
        // while eat out, fend-for-yourself, go-to-family, and vacation never
        // contribute to recipe metrics, even if a recipe id is attached.
        await SeedEntryAsync(
            new DateOnly(2026, 3, 4),
            CalendarEntryType.EatOut,
            recipeId: null,
            includeGroceries: false,
            snapshotDisplayText: "Sushi place");
        await SeedEntryAsync(new DateOnly(2026, 3, 5), CalendarEntryType.FendForYourself, null, false);
        await SeedEntryAsync(new DateOnly(2026, 3, 6), CalendarEntryType.GoToFamilyDinner, recipe.Id, false, "Pot Roast");
        await SeedEntryAsync(new DateOnly(2026, 3, 7), CalendarEntryType.Vacation, null, false);

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var top = MostEaten(root).Single();
        top.GetProperty("times_eaten").GetInt32().Should().Be(3);
        top.GetProperty("last_eaten").GetString().Should().Be("2026-03-03");
        root.GetProperty("never_eaten").GetInt32().Should().Be(0);
        // Every entry type counts as a meal, so the three recipe-carrying specials
        // plus the four non-occurrence days are seven meals this year.
        root.GetProperty("meal_count_year").GetInt32().Should().Be(7);
        root.GetProperty("protein_distribution").GetProperty("no_primary_protein").GetInt32().Should().Be(3);
        // All seven special-event types appear, including the recipe-carrying ones.
        EventCounts(root).Select(entry => entry.GetProperty("count").GetInt32())
            .Should().Equal(1, 1, 1, 1, 1, 1, 1);
    }

    // Historical integrity

    [Fact]
    public async Task A_hard_deleted_recipe_keeps_every_meal_and_no_total_drops()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var keep = await SeedRecipeAsync("Keep Me");
        var doomed = await SeedRecipeAsync("Doomed Stew");
        await SeedEntryAsync(new DateOnly(2026, 2, 1), CalendarEntryType.Recipe, doomed.Id, true, "Doomed Stew");
        await SeedEntryAsync(new DateOnly(2026, 2, 2), CalendarEntryType.Recipe, doomed.Id, true, "Doomed Stew");
        await SeedEntryAsync(new DateOnly(2026, 2, 3), CalendarEntryType.Recipe, doomed.Id, true, "Doomed Stew");
        await SeedEntryAsync(new DateOnly(2026, 2, 4), CalendarEntryType.Recipe, keep.Id, true, "Keep Me");

        using var client = factory.CreateClient();
        var before = await GetOverviewAsync(client);
        SumEaten(before).Should().Be(4);
        before.GetProperty("meal_count_year").GetInt32().Should().Be(4);
        before.GetProperty("never_eaten").GetInt32().Should().Be(0);

        // Simulates M12's hard delete: the recipes row is gone and each entry
        // falls back to its stored snapshot name (FK ON DELETE SET NULL).
        await DeleteRecipeAsync(doomed.Id);

        var after = await GetOverviewAsync(client);
        // No meal silently drops: the ranked total and the meal count survive.
        SumEaten(after).Should().Be(4);
        after.GetProperty("meal_count_year").GetInt32().Should().Be(4);
        after.GetProperty("never_eaten").GetInt32().Should().Be(0);

        var tombstone = MostEaten(after).Single(row => row.GetProperty("recipe_id").ValueKind == JsonValueKind.Null);
        tombstone.GetProperty("name").GetString().Should().Be("Doomed Stew");
        tombstone.GetProperty("status").GetString().Should().Be("deleted");
        tombstone.GetProperty("times_eaten").GetInt32().Should().Be(3);
        tombstone.GetProperty("last_eaten").GetString().Should().Be("2026-02-03");

        // A hard-deleted recipe has no live document: the per-recipe route is 404.
        (await client.GetAsync($"{RecipeMetricsPath}/{doomed.Id}", Ct)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_renamed_recipe_is_counted_once_and_never_double_counted()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var recipe = await SeedRecipeAsync("Old Stew");
        await SeedEntryAsync(new DateOnly(2026, 2, 1), CalendarEntryType.Recipe, recipe.Id, true, "Old Stew");
        await SeedEntryAsync(new DateOnly(2026, 3, 1), CalendarEntryType.Recipe, recipe.Id, true, "Old Stew");
        await RenameRecipeAsync(recipe.Id, "New Stew");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        // One identity (the recipe id wins), so one row holding both meals.
        var row = MostEaten(root).Single();
        row.GetProperty("name").GetString().Should().Be("New Stew");
        row.GetProperty("times_eaten").GetInt32().Should().Be(2);
        row.GetProperty("last_eaten").GetString().Should().Be("2026-03-01");
        root.GetProperty("meal_count_year").GetInt32().Should().Be(2);
        root.GetProperty("most_eaten").GetArrayLength().Should().Be(1);
    }

    // Household-local windows

    [Fact]
    public async Task The_calendar_year_is_the_household_year_not_utc()
    {
        // 2025-12-31T11:30Z is already 2026-01-01 in Auckland (UTC+13): a
        // UTC-naive implementation would compute today = 2025-12-31, put the
        // year window in 2025, and not count the meal as eaten.
        using var factory = await CreateSingleUserFactoryAsync(
            new TestTimeProvider(new DateTimeOffset(2025, 12, 31, 11, 30, 0, TimeSpan.Zero)),
            "Pacific/Auckland");
        var recipe = await SeedRecipeAsync("New Year Stew");
        await SeedEntryAsync(new DateOnly(2025, 12, 31), CalendarEntryType.Recipe, recipe.Id, true, "New Year Stew");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var windows = root.GetProperty("windows");
        windows.GetProperty("today").GetString().Should().Be("2026-01-01");
        windows.GetProperty("timezone").GetString().Should().Be("Pacific/Auckland");
        windows.GetProperty("year").GetProperty("label").GetString().Should().Be("2026");
        windows.GetProperty("year").GetProperty("start").GetString().Should().Be("2026-01-01");
        windows.GetProperty("year").GetProperty("end").GetString().Should().Be("2026-12-31");
        windows.GetProperty("month").GetProperty("label").GetString().Should().Be("January 2026");

        // The all-time ranking is unaffected by the window: the meal on the
        // household's yesterday still counts.
        var top = MostEaten(root).Single();
        top.GetProperty("times_eaten").GetInt32().Should().Be(1);
        top.GetProperty("last_eaten").GetString().Should().Be("2025-12-31");

        // The window itself is the 2026 household calendar year, so the meal is
        // outside both the year and the month counts.
        root.GetProperty("meal_count_year").GetInt32().Should().Be(0);
        root.GetProperty("meal_count_month").GetInt32().Should().Be(0);
        EventCounts(root).Should().OnlyContain(entry => entry.GetProperty("count").GetInt32() == 0);
    }

    [Fact]
    public async Task The_calendar_month_is_the_household_month_not_utc()
    {
        // 2026-02-28T12:00Z is 2026-03-01T01:00 in Auckland: the household month
        // is March 2026 while UTC is still February.
        using var factory = await CreateSingleUserFactoryAsync(
            new TestTimeProvider(new DateTimeOffset(2026, 2, 28, 12, 0, 0, TimeSpan.Zero)),
            "Pacific/Auckland");
        var recipe = await SeedRecipeAsync("Chili");
        await SeedEntryAsync(new DateOnly(2026, 2, 28), CalendarEntryType.Recipe, recipe.Id, true, "Chili");
        await SeedEntryAsync(new DateOnly(2026, 3, 20), CalendarEntryType.Recipe, recipe.Id, true, "Chili");

        using var client = factory.CreateClient();
        var root = await GetOverviewAsync(client);

        var windows = root.GetProperty("windows");
        windows.GetProperty("today").GetString().Should().Be("2026-03-01");
        windows.GetProperty("month").GetProperty("label").GetString().Should().Be("March 2026");
        windows.GetProperty("month").GetProperty("start").GetString().Should().Be("2026-03-01");
        windows.GetProperty("month").GetProperty("end").GetString().Should().Be("2026-03-31");

        // Only the March entry is in the household month; both 2026 meals are in
        // the household year, and the February meal is eaten.
        root.GetProperty("meal_count_month").GetInt32().Should().Be(1);
        root.GetProperty("meal_count_year").GetInt32().Should().Be(2);
        MostEaten(root).Single().GetProperty("last_eaten").GetString().Should().Be("2026-02-28");
    }

    // Rankings

    [Fact]
    public async Task Both_rankings_are_bounded_to_ten_with_deterministic_ties()
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
        var root = await GetOverviewAsync(client);

        // Twelve recipes each eaten once: the count ties break on the ordinal
        // name, and both rankings are capped at ten.
        MostEaten(root).Should().HaveCount(10);
        MostEaten(root).Select(row => row.GetProperty("name").GetString()).Should().Equal(
            "Recipe 00", "Recipe 01", "Recipe 02", "Recipe 03", "Recipe 04",
            "Recipe 05", "Recipe 06", "Recipe 07", "Recipe 08", "Recipe 09");

        // Least recently eaten is oldest last-eaten first, also capped at ten.
        LeastRecentlyEaten(root).Should().HaveCount(10);
        LeastRecentlyEaten(root).Select(row => row.GetProperty("name").GetString()).Should().Equal(
            "Recipe 00", "Recipe 01", "Recipe 02", "Recipe 03", "Recipe 04",
            "Recipe 05", "Recipe 06", "Recipe 07", "Recipe 08", "Recipe 09");
        root.GetProperty("never_eaten").GetInt32().Should().Be(0);
    }

    // Protein distribution

    [Fact]
    public async Task A_multi_protein_occurrence_counts_toward_every_protein_bar()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        var alpha = await SeedProteinAsync("Alpha");
        var beta = await SeedProteinAsync("Beta");
        var gamma = await SeedProteinAsync("Gamma");
        var multi = await SeedRecipeAsync("Alpha Beta Gamma");
        await SeedRecipeProteinAsync(multi.Id, alpha.Id);
        await SeedRecipeProteinAsync(multi.Id, beta.Id);
        await SeedRecipeProteinAsync(multi.Id, gamma.Id);
        var plain = await SeedRecipeAsync("Plain");
        await SeedEntryAsync(new DateOnly(2026, 3, 1), CalendarEntryType.Recipe, multi.Id, true, "Alpha Beta Gamma");
        await SeedEntryAsync(new DateOnly(2026, 3, 2), CalendarEntryType.Recipe, plain.Id, true, "Plain");
        await SeedEntryAsync(
            new DateOnly(2026, 3, 3),
            CalendarEntryType.EatOut,
            recipeId: null,
            includeGroceries: false,
            snapshotDisplayText: "Sushi place");

        using var client = factory.CreateClient();
        var distribution = (await GetOverviewAsync(client)).GetProperty("protein_distribution");
        var bars = distribution.GetProperty("proteins").EnumerateArray().ToList();

        // The multi-protein occurrence counts toward each protein, so the bars
        // sum to more than the occurrence count (the §1.12 lock).
        bars.Select(bar => (bar.GetProperty("name").GetString(), bar.GetProperty("count").GetInt32()))
            .Should().Equal(("Alpha", 1), ("Beta", 1), ("Gamma", 1));
        bars.Sum(bar => bar.GetProperty("count").GetInt32()).Should().Be(3);
        // Only the plain recipe is a recipe occurrence without a primary protein
        // the no-recipe eat-out day is excluded from the distribution entirely.
        distribution.GetProperty("no_primary_protein").GetInt32().Should().Be(1);
    }

    // Helpers

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private async Task<ApiFactory> CreateSingleUserFactoryAsync(
        TestTimeProvider clock,
        string timezone = "America/Chicago")
    {
        await SeedHouseholdAsync(AuthMode.SingleUser, timezone);
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

    private static List<JsonElement> MostEaten(JsonElement root) =>
        root.GetProperty("most_eaten").EnumerateArray().ToList();

    private static List<JsonElement> LeastRecentlyEaten(JsonElement root) =>
        root.GetProperty("least_recently_eaten").EnumerateArray().ToList();

    private static List<JsonElement> EventCounts(JsonElement root) =>
        root.GetProperty("special_event_counts").EnumerateArray().ToList();

    private static int SumEaten(JsonElement root) =>
        MostEaten(root).Sum(row => row.GetProperty("times_eaten").GetInt32());

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, string timezone)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M11 Battery Household",
            Timezone = timezone,
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

    private async Task DeleteRecipeAsync(long recipeId)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM recipes WHERE id = {recipeId}",
            Ct);
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
}
