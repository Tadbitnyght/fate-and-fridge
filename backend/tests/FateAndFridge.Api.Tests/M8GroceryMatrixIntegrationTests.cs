using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
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
/// Consolidated exhaustive M8 matrix (docs/M8_PLAN.md §4, §1.1–§1.16). This class
/// exists to close the gaps the per-issue suites did not cover, not to repeat
/// them: service-level fraction/format edge cases, the last-day-of-month range,
/// a custom range crossing a month boundary, the per-user remembered range, the
/// full eight-entry-type contribution matrix (including specials that do
/// <em>not</em> opt in), manual lines staying separate from a same-named recipe
/// bucket, household-wide staples across two users, check state surviving a
/// range expansion, per-user hide-checked, manual routes challenged before body
/// parsing, and household-local range validation on both the list and manual
/// create paths. The clock is injected so "today" is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M8GroceryListCollection.Name)]
public sealed class M8GroceryMatrixIntegrationTests(M8GroceryListFixture fixture) : IAsyncLifetime
{
    private const string GroceriesPath = "/api/v1/groceries";
    private const string ManualPath = "/api/v1/groceries/manual";
    private const string PreferencesPath = "/api/v1/groceries/preferences";
    private const string StaplesPath = "/api/v1/groceries/staples";
    private const string CheckPath = "/api/v1/groceries/check";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "grocery-matrix-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date, and comfortably
    // after the real host date so an injected clock never expires a session cookie.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Service-level display: exact fractions ---------------------------

    private static readonly IReadOnlyDictionary<string, ConversionUnit> SeedUnits =
        ReferenceDataSeedCatalog.Units.ToDictionary(
            unit => unit.Symbol,
            unit => new ConversionUnit(null, unit.Symbol, unit.Dimension, unit.BaseFactor),
            StringComparer.Ordinal);

    private readonly GroceryAggregationService _aggregation = new(new UnitConversionService());

    [Theory]
    [InlineData(1, 3, "1/3")]
    [InlineData(2, 3, "2/3")]
    [InlineData(3, 4, "3/4")]
    [InlineData(1, 4, "1/4")]
    [InlineData(1, 8, "1/8")]
    [InlineData(3, 8, "3/8")]
    [InlineData(5, 8, "5/8")]
    [InlineData(7, 8, "7/8")]
    public void Exact_cooking_fractions_render_as_fractions(int numerator, int denominator, string expected)
    {
        var quantity = (decimal)numerator / denominator;

        var items = _aggregation.Aggregate(
            [ServiceIngredient(1, "M8 Fraction Sugar", quantity, "cup", 10, "A", day: 6)]);

        items.Should().HaveCount(1);
        items[0].QuantityText.Should().Be(expected);
    }

    [Fact]
    public void Mass_units_combine_regardless_of_the_order_they_appear()
    {
        // The reverse of the existing 1 lb + 8 oz test: the smaller unit listed
        // first must still select and total into the largest present unit.
        var items = _aggregation.Aggregate(
        [
            ServiceIngredient(1, "M8 Order Beef", 8m, "oz", 10, "A", day: 6),
            ServiceIngredient(1, "M8 Order Beef", 1m, "lb", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Quantity.Should().Be(1.5m);
        items[0].UnitSymbol.Should().Be("lb");
        items[0].QuantityText.Should().Be("1 1/2");
    }

    [Fact]
    public void Volume_tablespoons_and_teaspoons_combine_into_tablespoons()
    {
        // Three teaspoons is one tablespoon, so 1 tbsp + 3 tsp totals 2 tbsp in
        // the largest present unit (tbsp = 3 tsp by seed factor).
        var items = _aggregation.Aggregate(
        [
            ServiceIngredient(1, "M8 Order Oil", 1m, "tbsp", 10, "A", day: 6),
            ServiceIngredient(1, "M8 Order Oil", 3m, "tsp", 20, "B", day: 7),
        ]);

        items.Should().HaveCount(1);
        items[0].Quantity.Should().Be(2m);
        items[0].UnitSymbol.Should().Be("tbsp");
        items[0].QuantityText.Should().Be("2");
    }

    // ---- Ranges: boundaries ----------------------------------------------

    [Fact]
    public async Task Rest_of_month_on_the_last_day_is_a_single_day()
    {
        // On the 30th, "Rest of Month" is exactly one day: start == end, count 1.
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 30, 18, 0, 0, TimeSpan.Zero));
        using var factory = await CreateSingleUserFactoryAsync(clock);
        using var client = factory.CreateClient();

        var range = (await GetRootAsync(client, $"{GroceriesPath}?preset=rest_of_month"))
            .GetProperty("range");

        range.GetProperty("start").GetString().Should().Be("2026-11-30");
        range.GetProperty("end").GetString().Should().Be("2026-11-30");
        range.GetProperty("day_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Custom_range_crosses_a_month_boundary()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var range = (await GetRootAsync(client, $"{GroceriesPath}?start=2026-11-28&days=7"))
            .GetProperty("range");

        range.GetProperty("preset").GetString().Should().Be(GroceryListService.PresetCustom);
        range.GetProperty("start").GetString().Should().Be("2026-11-28");
        range.GetProperty("end").GetString().Should().Be("2026-12-04");
        range.GetProperty("day_count").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task Remembered_range_is_per_user()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        var root = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var sam = await SeedUserAsync("sam", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(sam.Id, PermissionKeys.GroceriesView);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using var rootSession = await SignInAsync(factory, "root", AdminPassword);
        using var samSession = await SignInAsync(factory, "sam", MemberPassword);

        // Each user remembers their own last-used range through the API.
        (await SendJsonAsync(
            rootSession.Client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"range_preset":"next_14_days"}""",
            rootSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendJsonAsync(
            samSession.Client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"range_preset":"next_3_days"}""",
            samSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rootList = await GetRootAsync(rootSession.Client, GroceriesPath);
        rootList.GetProperty("range").GetProperty("preset").GetString().Should().Be("next_14_days");

        var samList = await GetRootAsync(samSession.Client, GroceriesPath);
        samList.GetProperty("range").GetProperty("preset").GetString().Should().Be("next_3_days");

        (await GetPreferenceAsync(root.Id))!.RangePreset.Should().Be("next_14_days");
        (await GetPreferenceAsync(sam.Id))!.RangePreset.Should().Be("next_3_days");
    }

    // ---- Scope: the full eight-entry-type matrix --------------------------

    [Theory]
    [InlineData(CalendarEntryType.Recipe, true, true)]
    [InlineData(CalendarEntryType.Recipe, false, false)]
    [InlineData(CalendarEntryType.HostFamilyDinner, true, true)]
    [InlineData(CalendarEntryType.Holiday, true, true)]
    [InlineData(CalendarEntryType.Misc, true, true)]
    [InlineData(CalendarEntryType.HostFamilyDinner, false, false)]
    [InlineData(CalendarEntryType.Holiday, false, false)]
    [InlineData(CalendarEntryType.Misc, false, false)]
    [InlineData(CalendarEntryType.EatOut, false, false)]
    [InlineData(CalendarEntryType.FendForYourself, false, false)]
    [InlineData(CalendarEntryType.GoToFamilyDinner, false, false)]
    [InlineData(CalendarEntryType.Vacation, false, false)]
    public async Task Entry_type_scope_matrix(
        CalendarEntryType entryType,
        bool includeGroceries,
        bool expectedToContribute)
    {
        var ingredient = await SeedIngredientAsync("M8 Matrix Scope", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Matrix Scope Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(
            Today,
            entryType,
            CarriesRecipe(entryType) ? recipe.Id : null,
            includeGroceries);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        if (expectedToContribute)
        {
            var item = AllItems(root).Single();
            item.GetProperty("quantity").GetDecimal().Should().Be(1m);
            item.GetProperty("sources").GetArrayLength().Should().Be(1);
        }
        else
        {
            root.GetProperty("departments").GetArrayLength().Should().Be(0);
        }
    }

    // ---- Manual vs recipe separation --------------------------------------

    [Fact]
    public async Task Manual_line_stays_separate_from_a_same_named_recipe_bucket()
    {
        var ingredient = await SeedIngredientAsync("M8 Matrix Duplicate", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Matrix Duplicate Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);
        await SeedManualItemAsync(
            "M8 Matrix Duplicate",
            ingredientId: null,
            departmentId: null,
            quantity: null,
            unitId: null,
            rangeStart: Today,
            rangeDays: 7);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var lines = ItemsNamed(root, "M8 Matrix Duplicate");
        lines.Should().HaveCount(2, "manual lines never merge into recipe buckets");

        var recipeLine = lines.Single(line => line.GetProperty("source_type").GetString() == "recipe");
        recipeLine.GetProperty("sources").GetArrayLength().Should().Be(1);
        recipeLine.GetProperty("manual_item_id").ValueKind.Should().Be(JsonValueKind.Null);

        var manualLine = lines.Single(line => line.GetProperty("source_type").GetString() == "manual");
        manualLine.GetProperty("sources").GetArrayLength().Should().Be(0);
        manualLine.GetProperty("manual_item_id").ValueKind.Should().Be(JsonValueKind.Number);
    }

    // ---- Staples are household-wide ---------------------------------------

    [Fact]
    public async Task Staples_are_household_wide_across_users()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var sam = await SeedUserAsync("sam", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(sam.Id, PermissionKeys.GroceriesEdit);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using var rootSession = await SignInAsync(factory, "root", AdminPassword);
        using var samSession = await SignInAsync(factory, "sam", MemberPassword);

        // root creates the staple; sam, a different user, sees the same catalog.
        var created = await SendJsonAsync(
            rootSession.Client,
            HttpMethod.Post,
            StaplesPath,
            """{"name":"M8 Matrix Household Staple"}""",
            rootSession.CsrfToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var stapleId = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        var catalog = await GetRootAsync(samSession.Client, StaplesPath);
        catalog.GetProperty("staples").EnumerateArray().Select(s => s.GetProperty("id").GetInt64())
            .Should().Contain(stapleId);

        // sam adds it to the list; root then sees the same manual line.
        var added = await SendJsonAsync(
            samSession.Client,
            HttpMethod.Post,
            $"{StaplesPath}/{stapleId}/add-to-list",
            JsonSerializer.Serialize(new { range_start = Format(Today), range_days = 7 }),
            samSession.CsrfToken);
        added.StatusCode.Should().Be(HttpStatusCode.Created);

        var rootList = await GetRootAsync(rootSession.Client, GroceriesPath);
        ItemsNamed(rootList, "M8 Matrix Household Staple").Should().HaveCount(1);
    }

    // ---- Check state across a range expansion -----------------------------

    [Fact]
    public async Task Checked_state_survives_a_range_expansion()
    {
        var ingredient = await SeedIngredientAsync("M8 Matrix Expand", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Matrix Expand Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        // Within Next 3 Days and Next 7 Days, so expanding the range keeps the line.
        await SeedEntryAsync(Today.AddDays(1), CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var narrow = await GetRootAsync(client, $"{GroceriesPath}?preset=next_3_days");
        var identity = ItemsNamed(narrow, "M8 Matrix Expand").Single()
            .GetProperty("identity").GetString()!;
        (await SendJsonAsync(
            client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity }),
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var widened = await GetRootAsync(client, $"{GroceriesPath}?preset=next_7_days");
        var line = ItemsNamed(widened, "M8 Matrix Expand").Single();
        line.GetProperty("identity").GetString().Should().Be(identity);
        line.GetProperty("checked").GetBoolean()
            .Should().BeTrue("recipe identity is quantity- and range-independent");
    }

    // ---- Hide-checked is per user -----------------------------------------

    [Fact]
    public async Task Hide_checked_is_per_user_and_persists()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        var root = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var sam = await SeedUserAsync("sam", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(sam.Id, PermissionKeys.GroceriesView);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using var rootSession = await SignInAsync(factory, "root", AdminPassword);
        using var samSession = await SignInAsync(factory, "sam", MemberPassword);

        (await SendJsonAsync(
            rootSession.Client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":true}""",
            rootSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendJsonAsync(
            samSession.Client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":false}""",
            samSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        // A fresh request per user reads back their own value.
        (await GetRootAsync(rootSession.Client, PreferencesPath))
            .GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        (await GetRootAsync(samSession.Client, PreferencesPath))
            .GetProperty("hide_checked").GetBoolean().Should().BeFalse();

        (await GetPreferenceAsync(root.Id))!.HideChecked.Should().BeTrue();
        (await GetPreferenceAsync(sam.Id))!.HideChecked.Should().BeFalse();
    }

    // ---- Permissions: manual writes challenged before body parsing --------

    [Fact]
    public async Task Anonymous_manual_patch_and_delete_return_unauthorized()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using var anonymous = factory.CreateClient();

        var patch = await SendJsonAsync(
            anonymous,
            HttpMethod.Patch,
            $"{ManualPath}/1",
            """{"name":"M8 Matrix Anon"}""",
            csrfToken: null);
        patch.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(patch)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);

        var delete = await SendJsonAsync(
            anonymous,
            HttpMethod.Delete,
            $"{ManualPath}/1",
            json: null,
            csrfToken: null);
        delete.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(delete)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);
    }

    // ---- Timezone: household-local today ----------------------------------

    [Fact]
    public async Task List_range_validation_uses_the_household_local_today()
    {
        // 02:00Z on 2026-11-16 is 20:00 on 2026-11-15 in America/Chicago, so the
        // household "today" is the 15th; a UTC-naive check would reject the 15th
        // as a past start and accept only the 16th.
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
        using var factory = await CreateSingleUserFactoryAsync(clock);
        using var client = factory.CreateClient();

        var accepted = await GetRootAsync(client, $"{GroceriesPath}?start=2026-11-15&days=3");
        accepted.GetProperty("range").GetProperty("start").GetString().Should().Be("2026-11-15");

        var rejected = await client.GetAsync($"{GroceriesPath}?start=2026-11-14&days=3", Ct);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(rejected)).GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("start", out _).Should().BeTrue(
                "household-yesterday is a past start in the household timezone");
    }

    [Fact]
    public async Task Manual_range_validation_uses_the_household_local_today()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
        using var factory = await CreateSingleUserFactoryAsync(clock);
        using var client = factory.CreateClient();

        var accepted = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            """{"name":"M8 Matrix TZ","range_start":"2026-11-15","range_days":7}""",
            csrfToken: null);
        accepted.StatusCode.Should().Be(HttpStatusCode.Created, "2026-11-15 is today in the household timezone");

        var rejected = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            """{"name":"M8 Matrix TZ","range_start":"2026-11-14","range_days":7}""",
            csrfToken: null);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(rejected)).GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("range_start", out _).Should().BeTrue(
                "household-yesterday is a past start in the household timezone");
    }

    // ---- Helpers ----------------------------------------------------------

    private static GroceryIngredientInput ServiceIngredient(
        long ingredientId,
        string name,
        decimal? quantity,
        string? unit,
        long recipeId,
        string recipeName,
        int day) =>
        new(
            ingredientId,
            name,
            quantity,
            unit is null ? null : SeedUnits[unit],
            recipeId,
            recipeName,
            new DateOnly(2026, 10, day));

    private static bool CarriesRecipe(CalendarEntryType entryType) =>
        entryType is CalendarEntryType.Recipe
            or CalendarEntryType.HostFamilyDinner
            or CalendarEntryType.Holiday
            or CalendarEntryType.Misc;

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

    private async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password)
    {
        var cookies = new CookieContainer();
        var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
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

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string? csrfToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> GetRootAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static List<JsonElement> AllItems(JsonElement root) =>
        root.GetProperty("departments").EnumerateArray()
            .SelectMany(department => department.GetProperty("items").EnumerateArray())
            .ToList();

    private static List<JsonElement> ItemsNamed(JsonElement root, string name) =>
        AllItems(root)
            .Where(item => item.GetProperty("name").GetString() == name)
            .ToList();

    private static string Format(DateOnly date) =>
        date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M8 Matrix Household",
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

    private async Task<UserGroceryPreference?> GetPreferenceAsync(long userId)
    {
        await using var context = fixture.CreateContext();
        return await context.UserGroceryPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(preference => preference.UserId == userId, Ct);
    }

    private async Task<Ingredient> SeedIngredientAsync(string name, long? departmentId)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var existing = await context.Ingredients
            .FirstOrDefaultAsync(ingredient => ingredient.NormalizedName == normalized.Normalized, Ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new Ingredient
        {
            CanonicalName = normalized.Display,
            NormalizedName = normalized.Normalized,
            DepartmentId = departmentId,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Ingredients.Add(created);
        await context.SaveChangesAsync(Ct);
        return created;
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

    private async Task SeedRecipeIngredientAsync(long recipeId, long ingredientId, decimal? quantity, long? unitId)
    {
        await using var context = fixture.CreateContext();
        context.RecipeIngredients.Add(new RecipeIngredient
        {
            RecipeId = recipeId,
            IngredientId = ingredientId,
            QuantityNumeric = quantity,
            UnitId = unitId,
            SortOrder = 0,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedEntryAsync(
        DateOnly date,
        CalendarEntryType entryType,
        long? recipeId,
        bool includeGroceries)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = entryType,
            RecipeId = recipeId,
            IncludeGroceries = includeGroceries,
            SnapshotRecipeName = null,
            SnapshotDisplayText = entryType.ToString(),
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedManualItemAsync(
        string name,
        long? ingredientId,
        long? departmentId,
        decimal? quantity,
        long? unitId,
        DateOnly rangeStart,
        int rangeDays)
    {
        await using var context = fixture.CreateContext();
        context.ManualGroceryItems.Add(new ManualGroceryItem
        {
            Name = name,
            IngredientId = ingredientId,
            DepartmentId = departmentId,
            QuantityNumeric = quantity,
            UnitId = unitId,
            RangeStart = rangeStart,
            RangeDays = rangeDays,
            SaveForFuture = false,
            CreatedByUserId = null,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error");

    private sealed class SignedInClient(HttpClient client, string csrfToken) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public string CsrfToken { get; } = csrfToken;

        public void Dispose() => Client.Dispose();
    }
}
