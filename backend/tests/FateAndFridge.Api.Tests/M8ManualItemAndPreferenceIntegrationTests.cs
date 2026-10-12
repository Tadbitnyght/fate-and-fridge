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
/// HTTP-level verification of the M8-04 manual-item CRUD and the per-user
/// preferences routes (docs/M8_PLAN.md §1.8, §1.11, §2.2): validation, unknown
/// references, the department default, the merge redirect, range-scoped
/// visibility, save-for-future staple promotion, delete check-state cleanup,
/// preference defaults/persistence/range normalization, single-user (system
/// actor) preference storage, and the permission matrix. The clock is injected so
/// "today" is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M8GroceryListCollection.Name)]
public sealed class M8ManualItemAndPreferenceIntegrationTests(M8GroceryListFixture fixture) : IAsyncLifetime
{
    private const string GroceriesPath = "/api/v1/groceries";
    private const string ManualPath = "/api/v1/groceries/manual";
    private const string PreferencesPath = "/api/v1/groceries/preferences";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "grocery-manual-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date, and comfortably
    // after the real host date so an injected clock never expires a session cookie.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Manual item create ----------------------------------------------

    [Fact]
    public async Task Manual_create_happy_path_returns_201_and_the_item()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("  M8 Manual Milk  ", quantity: 2.5m, saveForFuture: true),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await ReadJsonAsync(response);
        item.GetProperty("id").GetInt64().Should().BeGreaterThan(0);
        item.GetProperty("name").GetString().Should().Be("M8 Manual Milk");
        item.GetProperty("quantity_numeric").GetDecimal().Should().Be(2.5m);
        item.GetProperty("unit_id").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("ingredient_id").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("department_id").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("range_start").GetString().Should().Be(Format(Today));
        item.GetProperty("range_days").GetInt32().Should().Be(7);
        item.GetProperty("save_for_future").GetBoolean().Should().BeTrue();
        item.GetProperty("created_at").GetString().Should().NotBeNullOrEmpty();
        item.GetProperty("updated_at").GetString().Should().NotBeNullOrEmpty();

        var stored = await GetManualItemAsync(item.GetProperty("id").GetInt64());
        stored.Should().NotBeNull();
        stored!.CreatedByUserId.Should().BeNull("the single-user system actor has no user row");
        stored.SaveForFuture.Should().BeTrue();
        (await CountStaplesAsync()).Should().Be(1, "save_for_future promotes one staple");
    }

    [Theory]
    [InlineData("""{"range_start":"2026-11-15","range_days":7}""", "name")]
    [InlineData("""{"name":"   ","range_start":"2026-11-15","range_days":7}""", "name")]
    [InlineData("""{"name":"M8 X","range_start":"2026-11-15","range_days":7,"quantity_numeric":0}""", "quantity_numeric")]
    [InlineData("""{"name":"M8 X","range_start":"2026-11-14","range_days":7}""", "range_start")]
    [InlineData("""{"name":"M8 X","range_start":"2026-11-15","range_days":0}""", "range_days")]
    [InlineData("""{"name":"M8 X","range_start":"2026-11-15","range_days":367}""", "range_days")]
    [InlineData("""{"name":"M8 X","range_start":"2026-11-15","range_days":7,"unit_id":999999}""", "unit_id")]
    [InlineData("""{"name":"M8 X","range_start":"2026-11-15","range_days":7,"ingredient_id":999999}""", "ingredient_id")]
    [InlineData("""{"name":"M8 X","range_start":"2026-11-15","range_days":7,"department_id":999999}""", "department_id")]
    public async Task Manual_create_validation_failures(string json, string field)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(client, HttpMethod.Post, ManualPath, json, csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty(field, out _)
            .Should().BeTrue($"field {field} must be reported");
    }

    [Fact]
    public async Task Manual_create_rejects_an_overlong_name()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson(new string('x', 201)),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var fieldErrors = (await ReadErrorAsync(response))
            .GetProperty("details").GetProperty("field_errors");
        fieldErrors.TryGetProperty("name", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Manual_department_defaults_from_the_linked_ingredient()
    {
        var produce = await SeedDepartmentAsync("M8 Manual Produce", 1);
        var carrots = await SeedIngredientAsync("M8 Manual Carrots", produce.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("M8 Manual Carrots Bag", ingredientId: carrots.Id),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await ReadJsonAsync(response);
        item.GetProperty("ingredient_id").GetInt64().Should().Be(carrots.Id);
        item.GetProperty("department_id").GetInt64().Should().Be(produce.Id);
    }

    [Fact]
    public async Task Manual_create_redirects_a_merged_ingredient_to_the_canonical()
    {
        var target = await SeedIngredientAsync("M8 Manual Green Onion", departmentId: null);
        var source = await SeedIngredientAsync("M8 Manual Scallion", departmentId: null);
        await SeedMergeAsync(source.Id, target.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("M8 Manual Merged", ingredientId: source.Id),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(response)).GetProperty("ingredient_id").GetInt64().Should().Be(target.Id);
    }

    [Fact]
    public async Task Manual_item_is_visible_only_for_its_exact_range()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("M8 Manual Range Item"),
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var withinRange = await GetRootAsync(client, $"{GroceriesPath}?preset=next_7_days");
        ItemsNamed(withinRange, "M8 Manual Range Item").Should().HaveCount(1);
        ItemsNamed(withinRange, "M8 Manual Range Item").Single()
            .GetProperty("source_type").GetString().Should().Be("manual");

        var otherRange = await GetRootAsync(client, $"{GroceriesPath}?preset=next_14_days");
        ItemsNamed(otherRange, "M8 Manual Range Item").Should().BeEmpty();
    }

    // ---- Save for future --------------------------------------------------

    [Fact]
    public async Task Save_for_future_creates_one_staple_and_is_idempotent()
    {
        var lb = await SeedUnitAsync("lb", "pound", UnitDimension.Mass, 453.59237m);
        var produce = await SeedDepartmentAsync("M8 Staple Produce", 1);
        var carrots = await SeedIngredientAsync("M8 Staple Carrots", produce.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var first = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson(
                "M8 Staple Carrots",
                ingredientId: carrots.Id,
                quantity: 3m,
                unitId: lb.Id,
                saveForFuture: true),
            csrfToken: null);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        (await CountStaplesAsync()).Should().Be(1);
        var staple = await GetSingleStapleAsync();
        staple.Should().NotBeNull();
        staple!.Name.Should().Be("M8 Staple Carrots");
        staple.IngredientId.Should().Be(carrots.Id);
        staple.DepartmentId.Should().Be(produce.Id);
        staple.DefaultQuantityNumeric.Should().Be(3m);
        staple.UnitId.Should().Be(lb.Id);
        staple.IsActive.Should().BeTrue();

        // A different casing/whitespace name normalizes to the same staple.
        var second = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("  m8   staple   carrots ", saveForFuture: true),
            csrfToken: null);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        (await CountStaplesAsync()).Should().Be(1, "promotion is idempotent by normalized name");

        // Transitioning save_for_future to true on a fresh item promotes once.
        var third = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("M8 Staple Carrots", saveForFuture: false),
            csrfToken: null);
        third.StatusCode.Should().Be(HttpStatusCode.Created);
        var thirdId = (await ReadJsonAsync(third)).GetProperty("id").GetInt64();

        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ManualPath}/{thirdId}",
            """{"save_for_future":true}""",
            csrfToken: null);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(patched)).GetProperty("save_for_future").GetBoolean().Should().BeTrue();
        (await CountStaplesAsync()).Should().Be(1, "re-promotion does not duplicate the staple");
    }

    // ---- Manual item patch ------------------------------------------------

    [Fact]
    public async Task Manual_patch_updates_a_subset_and_clears_nullable_fields()
    {
        var lb = await SeedUnitAsync("lb", "pound", UnitDimension.Mass, 453.59237m);
        var produce = await SeedDepartmentAsync("M8 Patch Produce", 1);
        var ingredient = await SeedIngredientAsync("M8 Patch Ingredient", produce.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson(
                "M8 Patch Item",
                ingredientId: ingredient.Id,
                quantity: 3m,
                unitId: lb.Id),
            csrfToken: null);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        var partial = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ManualPath}/{id}",
            """{"name":"M8 Patch Renamed"}""",
            csrfToken: null);
        partial.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterPartial = await ReadJsonAsync(partial);
        afterPartial.GetProperty("name").GetString().Should().Be("M8 Patch Renamed");
        afterPartial.GetProperty("quantity_numeric").GetDecimal().Should().Be(3m);
        afterPartial.GetProperty("unit_id").GetInt64().Should().Be(lb.Id);
        afterPartial.GetProperty("ingredient_id").GetInt64().Should().Be(ingredient.Id);
        afterPartial.GetProperty("department_id").GetInt64().Should().Be(produce.Id);

        var cleared = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ManualPath}/{id}",
            """{"quantity_numeric":null,"unit_id":null,"ingredient_id":null,"department_id":null}""",
            csrfToken: null);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterClear = await ReadJsonAsync(cleared);
        afterClear.GetProperty("quantity_numeric").ValueKind.Should().Be(JsonValueKind.Null);
        afterClear.GetProperty("unit_id").ValueKind.Should().Be(JsonValueKind.Null);
        afterClear.GetProperty("ingredient_id").ValueKind.Should().Be(JsonValueKind.Null);
        afterClear.GetProperty("department_id").ValueKind.Should().Be(JsonValueKind.Null);

        var missing = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ManualPath}/999999",
            """{"name":"M8 Patch Missing"}""",
            csrfToken: null);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(missing)).GetProperty("code").GetString().Should().Be(ErrorCodes.NotFound);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"range_start":"2026-11-14"}""")]
    [InlineData("""{"range_days":367}""")]
    [InlineData("""{"unit_id":999999}""")]
    public async Task Manual_patch_validation_failures(string json)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("M8 Patch Validation Item"),
            csrfToken: null);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ManualPath}/{id}",
            json,
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ValidationFailed);
    }

    // ---- Manual item delete ----------------------------------------------

    [Fact]
    public async Task Manual_delete_removes_check_state_and_keeps_the_staple()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("M8 Delete Item", saveForFuture: true),
            csrfToken: null);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        var identity = GroceryItemIdentity.ForManualItem(id);
        await SeedCheckStateAsync(identity);
        (await CountCheckStatesAsync(identity)).Should().Be(1);
        (await CountStaplesAsync()).Should().Be(1);

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{ManualPath}/{id}",
            json: null,
            csrfToken: null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await GetManualItemAsync(id)).Should().BeNull();
        (await CountCheckStatesAsync(identity)).Should().Be(0, "the item's check state is removed");
        (await CountStaplesAsync()).Should().Be(1, "a promoted staple is retained");

        var again = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{ManualPath}/{id}",
            json: null,
            csrfToken: null);
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(again)).GetProperty("code").GetString().Should().Be(ErrorCodes.NotFound);
    }

    // ---- Preferences ------------------------------------------------------

    [Fact]
    public async Task Preferences_default_when_none_is_stored()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var preference = await GetRootAsync(client, PreferencesPath);

        preference.GetProperty("hide_checked").GetBoolean().Should().BeFalse();
        preference.GetProperty("range_preset").ValueKind.Should().Be(JsonValueKind.Null);
        preference.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        preference.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Preferences_patch_persists_hide_checked_and_drives_the_list_range()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":true,"range_preset":"next_14_days"}""",
            csrfToken: null);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(patched);
        body.GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        body.GetProperty("range_preset").GetString().Should().Be("next_14_days");
        body.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);

        var fetched = await GetRootAsync(client, PreferencesPath);
        fetched.GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        fetched.GetProperty("range_preset").GetString().Should().Be("next_14_days");

        var list = await GetRootAsync(client, GroceriesPath);
        list.GetProperty("range").GetProperty("preset").GetString().Should().Be("next_14_days");
    }

    [Fact]
    public async Task Preferences_custom_range_is_stored_and_used()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            JsonSerializer.Serialize(new
            {
                range_preset = "custom",
                range_start = Format(Today.AddDays(2)),
                range_days = 10,
            }),
            csrfToken: null);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(patched);
        body.GetProperty("range_preset").GetString().Should().Be("custom");
        body.GetProperty("range_start").GetString().Should().Be(Format(Today.AddDays(2)));
        body.GetProperty("range_days").GetInt32().Should().Be(10);

        var list = await GetRootAsync(client, GroceriesPath);
        var range = list.GetProperty("range");
        range.GetProperty("preset").GetString().Should().Be("custom");
        range.GetProperty("start").GetString().Should().Be(Format(Today.AddDays(2)));
        range.GetProperty("day_count").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Preferences_clearing_the_range_stores_all_three_as_null()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"range_preset":"next_3_days"}""",
            csrfToken: null);

        var cleared = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"range_preset":null}""",
            csrfToken: null);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(cleared);
        body.GetProperty("range_preset").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);

        var list = await GetRootAsync(client, GroceriesPath);
        list.GetProperty("range").GetProperty("preset").GetString().Should().Be("next_7_days");
    }

    [Theory]
    [InlineData("""{"range_preset":"custom"}""")]
    [InlineData("""{"range_preset":"custom","range_start":"2026-11-14","range_days":5}""")]
    [InlineData("""{"range_preset":"custom","range_start":"2026-11-15","range_days":367}""")]
    [InlineData("""{"range_preset":"bogus"}""")]
    [InlineData("""{"range_start":"2026-11-15","range_days":5}""")]
    [InlineData("""{}""")]
    public async Task Preferences_patch_validation_failures(string json)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(client, HttpMethod.Patch, PreferencesPath, json, csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Preferences_hide_checked_update_does_not_revalidate_a_stale_range()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        // A stale remembered custom range (its start is already past) must not
        // block an unrelated hide_checked toggle.
        await SeedSystemPreferenceAsync(
            """{"hide_checked":false,"range_preset":"custom","range_start":"2026-11-01","range_days":5}""");

        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":true}""",
            csrfToken: null);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(patched);
        body.GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        body.GetProperty("range_preset").GetString().Should().Be("custom");
        body.GetProperty("range_start").GetString().Should().Be("2026-11-01");
        body.GetProperty("range_days").GetInt32().Should().Be(5);

        // The stale range is unusable, so the list falls back to the default.
        var list = await GetRootAsync(client, GroceriesPath);
        list.GetProperty("range").GetProperty("preset").GetString().Should().Be("next_7_days");
    }

    // ---- Preferences: explicit-null range clearing (review FIX 1) ---------

    [Fact]
    public async Task Preferences_patch_accepts_the_documented_body_with_explicit_nulls()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        // The exact body docs/API.md documents as valid (a client echoing the
        // GET response): a fixed preset plus explicit null range fields.
        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":true,"range_preset":"next_7_days","range_start":null,"range_days":null}""",
            csrfToken: null);

        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(patched);
        body.GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        body.GetProperty("range_preset").GetString().Should().Be("next_7_days");
        body.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);

        var fetched = await GetRootAsync(client, PreferencesPath);
        fetched.GetProperty("range_preset").GetString().Should().Be("next_7_days");
        fetched.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        fetched.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Preferences_patch_clears_a_custom_range_when_returning_to_a_fixed_preset()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var custom = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            JsonSerializer.Serialize(new
            {
                range_preset = "custom",
                range_start = Format(Today.AddDays(2)),
                range_days = 10,
            }),
            csrfToken: null);
        custom.StatusCode.Should().Be(HttpStatusCode.OK);
        var withRange = await ReadJsonAsync(custom);
        withRange.GetProperty("range_preset").GetString().Should().Be("custom");
        withRange.GetProperty("range_start").GetString().Should().Be(Format(Today.AddDays(2)));
        withRange.GetProperty("range_days").GetInt32().Should().Be(10);

        var cleared = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"range_preset":"next_3_days","range_start":null,"range_days":null}""",
            csrfToken: null);

        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        var clearedBody = await ReadJsonAsync(cleared);
        clearedBody.GetProperty("range_preset").GetString().Should().Be("next_3_days");
        clearedBody.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        clearedBody.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);

        var fetched = await GetRootAsync(client, PreferencesPath);
        fetched.GetProperty("range_preset").GetString().Should().Be("next_3_days");
        fetched.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        fetched.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);

        // The single-user system actor's JSON store dropped the start/days too.
        var stored = await GetApplicationSettingJsonAsync(GroceryPreferenceService.SystemPreferenceKey);
        stored.Should().NotBeNull();
        using var document = JsonDocument.Parse(stored!);
        document.RootElement.GetProperty("range_preset").GetString().Should().Be("next_3_days");
        document.RootElement.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        document.RootElement.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("""{"range_preset":"next_7_days","range_start":"2026-11-15"}""")]
    [InlineData("""{"range_preset":"next_7_days","range_days":5}""")]
    [InlineData("""{"range_preset":"rest_of_month","range_start":"2026-11-15","range_days":5}""")]
    [InlineData("""{"range_preset":null,"range_start":"2026-11-15"}""")]
    [InlineData("""{"range_days":5}""")]
    public async Task Preferences_patch_rejects_a_non_null_range_without_a_custom_preset(string json)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(client, HttpMethod.Patch, PreferencesPath, json, csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("range_preset", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Preferences_patch_accepts_an_all_null_body_that_echoes_the_defaults()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        // The GET default body echoed straight back must not be rejected.
        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":false,"range_preset":null,"range_start":null,"range_days":null}""",
            csrfToken: null);

        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(patched);
        body.GetProperty("hide_checked").GetBoolean().Should().BeFalse();
        body.GetProperty("range_preset").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Preferences_patch_accepts_a_lone_explicit_null_range_as_a_clear()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        foreach (var json in new[] { """{"range_start":null}""", """{"range_days":null}""" })
        {
            var response = await SendJsonAsync(
                client,
                HttpMethod.Patch,
                PreferencesPath,
                json,
                csrfToken: null);

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                "only a non-null range value requires range_preset=custom");
        }

        var fetched = await GetRootAsync(client, PreferencesPath);
        fetched.GetProperty("range_preset").ValueKind.Should().Be(JsonValueKind.Null);
        fetched.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        fetched.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Preferences_custom_range_uses_the_household_local_today()
    {
        // 02:00Z on 2026-11-16 is 20:00 on 2026-11-15 in America/Chicago, so the
        // household "today" is the 15th; a UTC-naive check would use the 16th and
        // reject the 15th as a past start.
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero));
        using var factory = await CreateSingleUserFactoryAsync(clock);
        using var client = factory.CreateClient();

        var accepted = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            JsonSerializer.Serialize(new
            {
                range_preset = "custom",
                range_start = "2026-11-15",
                range_days = 5,
            }),
            csrfToken: null);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK, "2026-11-15 is today in the household timezone");
        (await ReadJsonAsync(accepted)).GetProperty("range_start").GetString().Should().Be("2026-11-15");

        var rejected = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            JsonSerializer.Serialize(new
            {
                range_preset = "custom",
                range_start = "2026-11-14",
                range_days = 5,
            }),
            csrfToken: null);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(rejected)).GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("range_start", out _).Should().BeTrue(
                "household-yesterday is a past start in the household timezone");
    }

    [Fact]
    public async Task Multiuser_preferences_patch_round_trips_through_one_row()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        var user = await SeedUserAsync("prefs", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(user.Id, PermissionKeys.GroceriesView);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using var session = await SignInAsync(factory, "prefs", MemberPassword);

        var patched = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":true,"range_preset":"next_3_days"}""",
            session.CsrfToken);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(patched);
        body.GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        body.GetProperty("range_preset").GetString().Should().Be("next_3_days");
        body.GetProperty("range_start").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("range_days").ValueKind.Should().Be(JsonValueKind.Null);

        var fetched = await GetRootAsync(session.Client, PreferencesPath);
        fetched.GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        fetched.GetProperty("range_preset").GetString().Should().Be("next_3_days");

        (await CountPreferencesAsync()).Should().Be(1, "exactly one per-user row is written");
        var stored = await GetUserPreferenceAsync(user.Id);
        stored.Should().NotBeNull();
        stored!.HideChecked.Should().BeTrue();
        stored.RangePreset.Should().Be("next_3_days");
        stored.RangeStart.Should().BeNull();
        stored.RangeDays.Should().BeNull();
    }

    [Fact]
    public async Task Groceries_read_does_not_create_a_preference_row()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        (await GetRootAsync(client, GroceriesPath)).GetProperty("range")
            .GetProperty("preset").GetString().Should().Be("next_7_days");

        (await CountPreferencesAsync()).Should().Be(0, "a read must not persist a preference");
        (await CountApplicationSettingAsync(GroceryPreferenceService.SystemPreferenceKey))
            .Should().Be(0, "a read must not persist the system actor's preference");
    }

    [Theory]
    [InlineData("""{"range_start":null}""", "range_start")]
    [InlineData("""{"range_days":null}""", "range_days")]
    public async Task Manual_patch_cannot_clear_the_range_fields(string json, string field)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ManualPath,
            ManualJson("M8 Patch Cannot Clear"),
            csrfToken: null);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ManualPath}/{id}",
            json,
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty(field, out _).Should().BeTrue();
    }

    // ---- Single-user system actor storage ---------------------------------
    [Fact]
    public async Task Single_user_preferences_persist_in_application_settings()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":true,"range_preset":"next_14_days"}""",
            csrfToken: null);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);

        // The system actor has no user row, so nothing is written to the
        // FK-bearing preferences table; the reserved settings key holds it.
        (await CountPreferencesAsync()).Should().Be(0);
        (await CountApplicationSettingAsync(GroceryPreferenceService.SystemPreferenceKey))
            .Should().Be(1);

        // A fresh client (no cookie) still reads the stored preference, and the
        // list honors the remembered range.
        using var second = factory.CreateClient();
        var fetched = await GetRootAsync(second, PreferencesPath);
        fetched.GetProperty("hide_checked").GetBoolean().Should().BeTrue();
        fetched.GetProperty("range_preset").GetString().Should().Be("next_14_days");

        var list = await GetRootAsync(second, GroceriesPath);
        list.GetProperty("range").GetProperty("preset").GetString().Should().Be("next_14_days");
    }

    // ---- Permissions ------------------------------------------------------

    [Fact]
    public async Task Permission_matrix_gates_manual_items_and_preferences()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var viewer = await SeedUserAsync("viewer", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(viewer.Id, PermissionKeys.GroceriesView);
        var editor = await SeedUserAsync("editor", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(editor.Id, PermissionKeys.GroceriesEdit);
        await SeedUserAsync("nobody", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var viewerSession = await SignInAsync(factory, "viewer", MemberPassword))
        {
            // View-only: reads and the preferences write are allowed, manual
            // writes are not.
            (await viewerSession.Client.GetAsync(GroceriesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
            (await viewerSession.Client.GetAsync(PreferencesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Patch,
                PreferencesPath,
                """{"hide_checked":true}""",
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Post,
                ManualPath,
                ManualJson("M8 Perm Viewer"),
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Patch,
                $"{ManualPath}/1",
                """{"name":"M8 Perm Viewer 2"}""",
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Delete,
                $"{ManualPath}/1",
                json: null,
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var editorSession = await SignInAsync(factory, "editor", MemberPassword))
        {
            // Edit implies view, so the preferences read is allowed too.
            (await editorSession.Client.GetAsync(PreferencesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);

            var created = await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Post,
                ManualPath,
                ManualJson("M8 Perm Editor"),
                editorSession.CsrfToken);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

            (await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Patch,
                $"{ManualPath}/{id}",
                """{"name":"M8 Perm Editor 2"}""",
                editorSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Delete,
                $"{ManualPath}/{id}",
                json: null,
                editorSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            (await deniedSession.Client.GetAsync(PreferencesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                deniedSession.Client,
                HttpMethod.Patch,
                PreferencesPath,
                """{"hide_checked":true}""",
                deniedSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                deniedSession.Client,
                HttpMethod.Post,
                ManualPath,
                ManualJson("M8 Perm Nobody"),
                deniedSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var anonymous = factory.CreateClient())
        {
            (await anonymous.GetAsync(PreferencesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
            var anonymousPatch = await SendJsonAsync(
                anonymous,
                HttpMethod.Patch,
                PreferencesPath,
                """{"hide_checked":true}""",
                csrfToken: null);
            anonymousPatch.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadErrorAsync(anonymousPatch)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.AuthRequired);
            (await SendJsonAsync(
                anonymous,
                HttpMethod.Post,
                ManualPath,
                ManualJson("M8 Perm Anon"),
                csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
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

    private static string ManualJson(
        string name,
        long? ingredientId = null,
        long? departmentId = null,
        decimal? quantity = null,
        long? unitId = null,
        bool saveForFuture = false,
        DateOnly? rangeStart = null,
        int rangeDays = 7)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["range_start"] = Format(rangeStart ?? Today),
            ["range_days"] = rangeDays,
            ["save_for_future"] = saveForFuture,
        };

        if (ingredientId is { } ingredient)
        {
            payload["ingredient_id"] = ingredient;
        }

        if (departmentId is { } department)
        {
            payload["department_id"] = department;
        }

        if (quantity is { } amount)
        {
            payload["quantity_numeric"] = amount;
        }

        if (unitId is { } unit)
        {
            payload["unit_id"] = unit;
        }

        return JsonSerializer.Serialize(payload);
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
            HouseholdName = "M8 Manual Household",
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

    private async Task<GroceryDepartment> SeedDepartmentAsync(string name, int sortOrder)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var existing = await context.GroceryDepartments
            .FirstOrDefaultAsync(department => department.NormalizedName == normalized.Normalized, Ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new GroceryDepartment
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            SortOrder = sortOrder,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.GroceryDepartments.Add(created);
        await context.SaveChangesAsync(Ct);
        return created;
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

    private async Task SeedMergeAsync(long sourceId, long targetId)
    {
        await using var context = fixture.CreateContext();
        var source = await context.Ingredients.SingleAsync(ingredient => ingredient.Id == sourceId, Ct);
        source.MergedIntoIngredientId = targetId;
        source.IsActive = false;
        await context.SaveChangesAsync(Ct);
    }

    private async Task<Unit> SeedUnitAsync(
        string symbol,
        string displayName,
        UnitDimension dimension,
        decimal? baseFactor)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(displayName)!;
        await using var context = fixture.CreateContext();
        var existing = await context.Units
            .FirstOrDefaultAsync(
                unit => unit.Symbol == symbol || unit.NormalizedName == normalized.Normalized,
                Ct);
        if (existing is not null)
        {
            return existing;
        }

        var created = new Unit
        {
            Symbol = symbol,
            DisplayName = displayName,
            NormalizedName = normalized.Normalized,
            Dimension = dimension,
            BaseFactor = baseFactor,
            IsCustom = false,
            IsActive = true,
        };
        context.Units.Add(created);
        await context.SaveChangesAsync(Ct);
        return created;
    }

    private async Task SeedCheckStateAsync(string identity)
    {
        await using var context = fixture.CreateContext();
        context.GroceryCheckStates.Add(new GroceryCheckState
        {
            ItemIdentityHash = identity,
            Checked = true,
            CheckedByUserId = null,
            CheckedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<ManualGroceryItem?> GetManualItemAsync(long id)
    {
        await using var context = fixture.CreateContext();
        return await context.ManualGroceryItems
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, Ct);
    }

    private async Task<GroceryStaple?> GetSingleStapleAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.GroceryStaples.AsNoTracking().SingleOrDefaultAsync(Ct);
    }

    private async Task<int> CountStaplesAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.GroceryStaples.CountAsync(Ct);
    }

    private async Task<int> CountCheckStatesAsync(string identity)
    {
        await using var context = fixture.CreateContext();
        return await context.GroceryCheckStates
            .CountAsync(state => state.ItemIdentityHash == identity, Ct);
    }

    private async Task<int> CountPreferencesAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.UserGroceryPreferences.CountAsync(Ct);
    }

    private async Task<int> CountApplicationSettingAsync(string key)
    {
        await using var context = fixture.CreateContext();
        return await context.ApplicationSettings.CountAsync(setting => setting.Key == key, Ct);
    }

    private async Task SeedSystemPreferenceAsync(string valueJson)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({GroceryPreferenceService.SystemPreferenceKey}, {valueJson}::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = EXCLUDED.value_json, updated_at = EXCLUDED.updated_at
            """,
            Ct);
    }

    private async Task<UserGroceryPreference?> GetUserPreferenceAsync(long userId)
    {
        await using var context = fixture.CreateContext();
        return await context.UserGroceryPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(preference => preference.UserId == userId, Ct);
    }

    private async Task<string?> GetApplicationSettingJsonAsync(string key)
    {
        await using var context = fixture.CreateContext();
        return await context.ApplicationSettings
            .AsNoTracking()
            .Where(setting => setting.Key == key)
            .Select(setting => setting.ValueJson)
            .SingleOrDefaultAsync(Ct);
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
