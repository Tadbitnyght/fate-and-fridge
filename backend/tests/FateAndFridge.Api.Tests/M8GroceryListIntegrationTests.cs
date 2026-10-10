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
/// HTTP-level verification of the M8-03 grocery list read route
/// (docs/M8_PLAN.md §1.3–§1.12, §2.2): preset/custom range resolution and
/// validation, the remembered range, the include_groceries contribution scope,
/// aggregation through the list, per-meal attribution, manual-item range
/// scoping, department/checked ordering, check state, and the permission matrix.
/// The clock is injected so "today" is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M8GroceryListCollection.Name)]
public sealed class M8GroceryListIntegrationTests(M8GroceryListFixture fixture) : IAsyncLifetime
{
    private const string GroceriesPath = "/api/v1/groceries";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "grocery-list-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date, and comfortably
    // after the real host date so an injected clock never expires a session cookie.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Range resolution -------------------------------------------------

    [Theory]
    [InlineData("next_3_days", 3)]
    [InlineData("next_7_days", 7)]
    [InlineData("next_14_days", 14)]
    public async Task Preset_resolves_to_its_fixed_length(string preset, int days)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var root = await GetRootAsync(client, $"{GroceriesPath}?preset={preset}");

        var range = root.GetProperty("range");
        range.GetProperty("preset").GetString().Should().Be(preset);
        range.GetProperty("start").GetString().Should().Be(Format(Today));
        range.GetProperty("end").GetString().Should().Be(Format(Today.AddDays(days - 1)));
        range.GetProperty("day_count").GetInt32().Should().Be(days);
    }

    [Fact]
    public async Task Rest_of_month_ends_on_the_month_boundary()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var root = await GetRootAsync(client, $"{GroceriesPath}?preset=rest_of_month");

        var range = root.GetProperty("range");
        range.GetProperty("start").GetString().Should().Be("2026-11-15");
        range.GetProperty("end").GetString().Should().Be("2026-11-30");
        range.GetProperty("day_count").GetInt32().Should().Be(16);
    }

    [Fact]
    public async Task Next_seven_days_crosses_a_month_boundary()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 28, 18, 0, 0, TimeSpan.Zero));
        using var factory = await CreateSingleUserFactoryAsync(clock);
        using var client = factory.CreateClient();

        var root = await GetRootAsync(client, $"{GroceriesPath}?preset=next_7_days");

        var range = root.GetProperty("range");
        range.GetProperty("start").GetString().Should().Be("2026-11-28");
        range.GetProperty("end").GetString().Should().Be("2026-12-04");
        range.GetProperty("day_count").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task Custom_range_uses_start_and_day_count()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var root = await GetRootAsync(client, $"{GroceriesPath}?start={Format(Today.AddDays(2))}&days=5");

        var range = root.GetProperty("range");
        range.GetProperty("preset").GetString().Should().Be(GroceryListService.PresetCustom);
        range.GetProperty("start").GetString().Should().Be(Format(Today.AddDays(2)));
        range.GetProperty("end").GetString().Should().Be(Format(Today.AddDays(6)));
        range.GetProperty("day_count").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Past_start_is_rejected()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"{GroceriesPath}?start={Format(Today.AddDays(-1))}&days=3", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty("start", out _)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Range_larger_than_366_days_is_rejected()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{GroceriesPath}?start={Format(Today)}&days=367", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.GroceryRangeTooLarge);
    }

    [Fact]
    public async Task Days_equal_to_366_is_allowed_while_367_is_rejected()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var allowed = await GetRootAsync(client, $"{GroceriesPath}?start={Format(Today)}&days=366");
        allowed.GetProperty("range").GetProperty("day_count").GetInt32().Should().Be(366);
        allowed.GetProperty("range").GetProperty("end").GetString()
            .Should().Be(Format(Today.AddDays(365)));

        var rejected = await client.GetAsync($"{GroceriesPath}?start={Format(Today)}&days=367", Ct);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(rejected)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.GroceryRangeTooLarge);
    }

    [Fact]
    public async Task Preset_cannot_be_combined_with_start_or_days()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"{GroceriesPath}?preset=next_7_days&start={Format(Today)}&days=3", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Unknown_preset_is_rejected()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{GroceriesPath}?preset=bogus", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty("preset", out _)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Days_without_start_is_rejected()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{GroceriesPath}?days=3", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task No_query_defaults_to_next_seven_days()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var root = await GetRootAsync(client, GroceriesPath);

        root.GetProperty("range").GetProperty("preset").GetString()
            .Should().Be(GroceryListService.PresetNext7Days);
    }

    [Fact]
    public async Task Remembered_preference_is_used_when_no_query_is_supplied()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedPreferenceAsync(admin.Id, GroceryListService.PresetNext14Days, null, null);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var remembered = await GetRootAsync(session.Client, GroceriesPath);
        remembered.GetProperty("range").GetProperty("preset").GetString()
            .Should().Be(GroceryListService.PresetNext14Days);

        await SeedPreferenceAsync(admin.Id, GroceryListService.PresetCustom, Today.AddDays(2), 10);
        var custom = await GetRootAsync(session.Client, GroceriesPath);
        var range = custom.GetProperty("range");
        range.GetProperty("preset").GetString().Should().Be(GroceryListService.PresetCustom);
        range.GetProperty("start").GetString().Should().Be(Format(Today.AddDays(2)));
        range.GetProperty("day_count").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Read_does_not_create_or_update_the_preference()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        (await CountPreferencesAsync()).Should().Be(0);

        var root = await GetRootAsync(session.Client, GroceriesPath);
        root.GetProperty("range").GetProperty("preset").GetString()
            .Should().Be(GroceryListService.PresetNext7Days);
        (await CountPreferencesAsync()).Should().Be(0, "a read must not create a preference row");

        await SeedPreferenceAsync(admin.Id, GroceryListService.PresetNext14Days, null, null);
        var before = await GetPreferenceAsync(admin.Id);

        var remembered = await GetRootAsync(session.Client, GroceriesPath);
        remembered.GetProperty("range").GetProperty("preset").GetString()
            .Should().Be(GroceryListService.PresetNext14Days);

        var after = await GetPreferenceAsync(admin.Id);
        after.Should().NotBeNull();
        after!.RangePreset.Should().Be(before!.RangePreset);
        after.RangeStart.Should().Be(before.RangeStart);
        after.RangeDays.Should().Be(before.RangeDays);
        after.UpdatedAt.Should().Be(before.UpdatedAt, "a read must not touch the preference row");
    }

    // ---- Contribution scope ----------------------------------------------

    [Fact]
    public async Task Only_include_groceries_entries_contribute()
    {
        var ingredient = await SeedIngredientAsync("M8 Scope Included", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Scope Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);
        await SeedEntryAsync(Today.AddDays(1), CalendarEntryType.Recipe, recipe.Id, includeGroceries: false);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var item = AllItems(root).Single();
        item.GetProperty("quantity").GetDecimal().Should().Be(1m);
        item.GetProperty("sources").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Non_grocery_entry_types_contribute_nothing()
    {
        var ingredient = await SeedIngredientAsync("M8 Scope Excluded", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Scope Excluded Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.EatOut, null, includeGroceries: false);
        await SeedEntryAsync(Today.AddDays(1), CalendarEntryType.FendForYourself, null, includeGroceries: false);
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.GoToFamilyDinner, null, includeGroceries: false);
        await SeedEntryAsync(Today.AddDays(3), CalendarEntryType.Vacation, null, includeGroceries: false);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        root.GetProperty("departments").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Special_entries_with_a_recipe_contribute()
    {
        var ingredient = await SeedIngredientAsync("M8 Scope Special", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Scope Special Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.HostFamilyDinner, recipe.Id, includeGroceries: true);
        await SeedEntryAsync(Today.AddDays(1), CalendarEntryType.Holiday, recipe.Id, includeGroceries: true);
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.Misc, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var item = AllItems(root).Single();
        item.GetProperty("quantity").GetDecimal().Should().Be(3m);
        item.GetProperty("sources").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Retired_recipe_still_contributes_via_its_ingredient_rows()
    {
        var ingredient = await SeedIngredientAsync("M8 Retired Ingredient", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Retired Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 2m, unitId: null);
        await SeedRetireRecipeAsync(recipe.Id);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var item = AllItems(root).Single();
        item.GetProperty("name").GetString().Should().Be("M8 Retired Ingredient");
        item.GetProperty("quantity").GetDecimal().Should().Be(2m);
    }

    // ---- Aggregation through the list ------------------------------------

    [Fact]
    public async Task Compatible_mass_units_combine_into_the_largest_present_unit()
    {
        var lb = await SeedUnitAsync("lb", "pound", UnitDimension.Mass, 453.59237m);
        var oz = await SeedUnitAsync("oz", "ounce", UnitDimension.Mass, 28.349523125m);
        var ingredient = await SeedIngredientAsync("M8 Combine Flour", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Combine Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: lb.Id);
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 8m, unitId: oz.Id);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var item = AllItems(root).Single();
        item.GetProperty("quantity_text").GetString().Should().Be("1 1/2");
        item.GetProperty("quantity").GetDecimal().Should().Be(1.5m);
        item.GetProperty("unit").GetString().Should().Be("lb");
    }

    [Fact]
    public async Task Incompatible_dimensions_stay_separate()
    {
        var cup = await SeedUnitAsync("cup", "cup", UnitDimension.Volume, 236.5882365m);
        var ingredient = await SeedIngredientAsync("M8 Onion", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Onion Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 0.5m, unitId: cup.Id);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var items = ItemsNamed(root, "M8 Onion");
        items.Should().HaveCount(2);
        items.Should().Contain(item => item.GetProperty("unit").ValueKind == JsonValueKind.Null
            && item.GetProperty("quantity_text").GetString() == "1");
        items.Should().Contain(item => item.GetProperty("unit").GetString() == "cup"
            && item.GetProperty("quantity_text").GetString() == "1/2");
    }

    [Fact]
    public async Task Quantity_less_recipe_line_keeps_its_display_unit()
    {
        var tsp = await SeedUnitAsync("tsp", "teaspoon", UnitDimension.Volume, 4.92892159375m);
        var ingredient = await SeedIngredientAsync("M8 To Taste Spice", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 To Taste Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: null, unitId: tsp.Id);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var item = AllItems(root).Single();
        item.GetProperty("quantity").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("quantity_text").GetString().Should().BeEmpty();
        item.GetProperty("unit").GetString().Should().Be("tsp");
    }

    [Fact]
    public async Task Alias_resolves_to_canonical_through_a_merge_redirect()
    {
        var target = await SeedIngredientAsync("M8 Green Onion", departmentId: null);
        var source = await SeedIngredientAsync("M8 Scallion", departmentId: null);
        await SeedMergeAsync(source.Id, target.Id);
        var recipe = await SeedRecipeAsync("M8 Merge Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, source.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, target.Id, quantity: 2m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var item = AllItems(root).Single();
        item.GetProperty("name").GetString().Should().Be("M8 Green Onion");
        item.GetProperty("ingredient_id").GetInt64().Should().Be(target.Id);
        item.GetProperty("quantity").GetDecimal().Should().Be(3m);
    }

    [Fact]
    public async Task Each_contributing_meal_produces_one_attribution_row()
    {
        var ingredient = await SeedIngredientAsync("M8 Attribute Ingredient", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Attribute Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var sources = AllItems(root).Single().GetProperty("sources").EnumerateArray().ToList();
        sources.Should().HaveCount(2);
        sources.Select(source => source.GetProperty("date").GetString())
            .Should().Equal(Format(Today), Format(Today.AddDays(2)));
        sources.Should().OnlyContain(source =>
            source.GetProperty("recipe_id").GetInt64() == recipe.Id
            && source.GetProperty("recipe_name").GetString() == "M8 Attribute Recipe");
    }

    // ---- Manual items -----------------------------------------------------

    [Fact]
    public async Task Manual_items_show_only_for_the_exact_active_range()
    {
        await SeedManualItemAsync(
            "M8 Manual Range",
            ingredientId: null,
            departmentId: null,
            quantity: 2m,
            unitId: null,
            rangeStart: Today,
            rangeDays: 7);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var withinRange = await GetRootAsync(client, GroceriesPath);
        ItemsNamed(withinRange, "M8 Manual Range").Should().HaveCount(1);
        ItemsNamed(withinRange, "M8 Manual Range").Single()
            .GetProperty("source_type").GetString().Should().Be("manual");

        var otherRange = await GetRootAsync(client, $"{GroceriesPath}?preset=next_14_days");
        ItemsNamed(otherRange, "M8 Manual Range").Should().BeEmpty();
    }

    [Fact]
    public async Task Manual_department_defaults_to_the_linked_ingredient_and_other_is_last()
    {
        var produce = await SeedDepartmentAsync("M8 Produce", sortOrder: 1);
        var carrots = await SeedIngredientAsync("M8 Carrots", produce.Id);
        var recipe = await SeedRecipeAsync("M8 Carrots Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, carrots.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);
        await SeedManualItemAsync(
            "M8 Paper Towels", ingredientId: null, departmentId: null,
            quantity: null, unitId: null, rangeStart: Today, rangeDays: 7);
        await SeedManualItemAsync(
            "M8 Milk", ingredientId: carrots.Id, departmentId: null,
            quantity: 1m, unitId: null, rangeStart: Today, rangeDays: 7);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var departments = root.GetProperty("departments").EnumerateArray().ToList();
        departments.Should().HaveCount(2);

        var produceGroup = departments[0];
        produceGroup.GetProperty("name").GetString().Should().Be("M8 Produce");
        produceGroup.GetProperty("is_other").GetBoolean().Should().BeFalse();
        DepartmentItemsNamed(produceGroup, "M8 Carrots").Should().HaveCount(1);
        DepartmentItemsNamed(produceGroup, "M8 Milk").Should().HaveCount(1);

        var other = departments[1];
        other.GetProperty("is_other").GetBoolean().Should().BeTrue();
        other.GetProperty("department_id").ValueKind.Should().Be(JsonValueKind.Null);
        DepartmentItemsNamed(other, "M8 Paper Towels").Should().HaveCount(1);
    }

    [Fact]
    public async Task Manual_quantity_is_formatted_and_quantity_less_lines_render_empty()
    {
        var lb = await SeedUnitAsync("lb", "pound", UnitDimension.Mass, 453.59237m);
        await SeedManualItemAsync(
            "M8 Cheese", ingredientId: null, departmentId: null,
            quantity: 1.5m, unitId: lb.Id, rangeStart: Today, rangeDays: 7);
        await SeedManualItemAsync(
            "M8 Napkins", ingredientId: null, departmentId: null,
            quantity: null, unitId: null, rangeStart: Today, rangeDays: 7);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var cheese = ItemsNamed(root, "M8 Cheese").Single();
        cheese.GetProperty("quantity_text").GetString().Should().Be("1 1/2");
        cheese.GetProperty("quantity").GetDecimal().Should().Be(1.5m);
        cheese.GetProperty("unit").GetString().Should().Be("lb");
        cheese.GetProperty("manual_item_id").ValueKind.Should().Be(JsonValueKind.Number);
        cheese.GetProperty("sources").GetArrayLength().Should().Be(0);

        var napkins = ItemsNamed(root, "M8 Napkins").Single();
        napkins.GetProperty("quantity_text").GetString().Should().BeEmpty();
        napkins.GetProperty("quantity").ValueKind.Should().Be(JsonValueKind.Null);
        napkins.GetProperty("unit").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- Ordering and check state ----------------------------------------

    [Fact]
    public async Task Departments_follow_configured_sort_order()
    {
        var second = await SeedDepartmentAsync("M8 Zoology", sortOrder: 2);
        var first = await SeedDepartmentAsync("M8 Bakery", sortOrder: 1);
        var flour = await SeedIngredientAsync("M8 Order Flour", first.Id);
        var zebra = await SeedIngredientAsync("M8 Zebra Beans", second.Id);
        var recipe = await SeedRecipeAsync("M8 Order Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, flour.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, zebra.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var root = await GetRootAsync(client, GroceriesPath);

        var names = root.GetProperty("departments").EnumerateArray()
            .Select(department => department.GetProperty("name").GetString())
            .ToArray();
        names.Should().Equal("M8 Bakery", "M8 Zoology");
    }

    [Fact]
    public async Task Items_sort_unchecked_first_alphabetically_then_checked_last()
    {
        var bakery = await SeedDepartmentAsync("M8 Checked Bakery", sortOrder: 1);
        var apple = await SeedIngredientAsync("M8 Apple", bakery.Id);
        var banana = await SeedIngredientAsync("M8 Banana", bakery.Id);
        var cherry = await SeedIngredientAsync("M8 Cherry", bakery.Id);
        var recipe = await SeedRecipeAsync("M8 Checked Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, apple.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, banana.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, cherry.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var before = await GetRootAsync(client, GroceriesPath);
        var appleIdentity = ItemsNamed(before, "M8 Apple").Single()
            .GetProperty("identity").GetString()!;

        await SeedCheckStateAsync(appleIdentity);

        var after = await GetRootAsync(client, GroceriesPath);
        var ordered = AllItems(after)
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();
        ordered.Should().Equal("M8 Banana", "M8 Cherry", "M8 Apple");

        ItemsNamed(after, "M8 Apple").Single().GetProperty("checked").GetBoolean().Should().BeTrue();
        ItemsNamed(after, "M8 Banana").Single().GetProperty("checked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Check_state_is_keyed_by_identity_across_the_list()
    {
        var ingredient = await SeedIngredientAsync("M8 Check Identity", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 Check Identity Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, includeGroceries: true);

        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var first = await GetRootAsync(client, GroceriesPath);
        first.GetProperty("departments").EnumerateArray()
            .SelectMany(department => department.GetProperty("items").EnumerateArray())
            .Single().GetProperty("checked").GetBoolean().Should().BeFalse();

        await SeedCheckStateAsync(
            AllItems(first).Single().GetProperty("identity").GetString()!);

        var second = await GetRootAsync(client, GroceriesPath);
        AllItems(second).Single().GetProperty("checked").GetBoolean().Should().BeTrue();
    }

    // ---- Permissions ------------------------------------------------------

    [Fact]
    public async Task Permission_matrix_gates_the_read()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var viewer = await SeedUserAsync("viewer", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(viewer.Id, PermissionKeys.GroceriesView);
        await SeedUserAsync("nobody", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var viewerSession = await SignInAsync(factory, "viewer", MemberPassword))
        {
            (await viewerSession.Client.GetAsync(GroceriesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            (await deniedSession.Client.GetAsync(GroceriesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden);
        }

        using (var adminSession = await SignInAsync(factory, "root", AdminPassword))
        {
            (await adminSession.Client.GetAsync(GroceriesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(GroceriesPath, Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Single_user_system_actor_reads_without_a_session()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(GroceriesPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
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

    private static List<JsonElement> DepartmentItemsNamed(JsonElement department, string name) =>
        department.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("name").GetString() == name)
            .ToList();

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M8 Grocery Household",
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

    private async Task SeedPreferenceAsync(long userId, string preset, DateOnly? start, int? days)
    {
        await using var context = fixture.CreateContext();
        var existing = await context.UserGroceryPreferences
            .SingleOrDefaultAsync(preference => preference.UserId == userId, Ct);
        if (existing is not null)
        {
            context.UserGroceryPreferences.Remove(existing);
        }

        context.UserGroceryPreferences.Add(new UserGroceryPreference
        {
            UserId = userId,
            HideChecked = false,
            RangePreset = preset,
            RangeStart = start,
            RangeDays = days,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<int> CountPreferencesAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.UserGroceryPreferences.CountAsync(Ct);
    }

    private async Task<UserGroceryPreference?> GetPreferenceAsync(long userId)
    {
        await using var context = fixture.CreateContext();
        return await context.UserGroceryPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(preference => preference.UserId == userId, Ct);
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
        // The startup seeder keys a unit's normalized name off its display name
        // ("lb" → "pound"), so match on either the symbol or the normalized
        // display name to reuse the seeded row instead of colliding on the
        // unique symbol.
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

    private async Task SeedRetireRecipeAsync(long recipeId)
    {
        await using var context = fixture.CreateContext();
        var recipe = await context.Recipes.SingleAsync(candidate => candidate.Id == recipeId, Ct);
        recipe.Status = RecipeStatus.Retired;
        await context.SaveChangesAsync(Ct);
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
