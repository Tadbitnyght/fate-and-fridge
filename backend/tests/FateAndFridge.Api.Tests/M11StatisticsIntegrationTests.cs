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
/// HTTP-level verification of the M11-01 statistics overview
/// (docs/M11_PLAN.md §1.3, §1.4, §1.12, §1.13, §1.19, §1.20, §1.24, §2.2):
/// household-local windows, the strictly-past eaten rule, retired/deleted
/// snapshot integrity, the two rankings with their deterministic ordering, the
/// month/year counts, the primary-protein distribution, the special-event
/// counts, and the <c>recipes.view</c> permission matrix. The clock is injected
/// so "today" is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M11StatisticsCollection.Name)]
public sealed class M11StatisticsIntegrationTests(M11StatisticsFixture fixture) : IAsyncLifetime
{
    private const string OverviewPath = "/api/v1/statistics/overview";
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
