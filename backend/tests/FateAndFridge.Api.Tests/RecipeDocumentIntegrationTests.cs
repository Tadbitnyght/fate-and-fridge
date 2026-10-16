using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the M3-05 … M3-09 recipe document (docs/M3_PLAN.md
/// §1–§2): ingredient groups and ingredients, instruction sections and steps,
/// protein/tag associations, decimal/`to taste` handling, retired-reference
/// rules, reference-data delete safety, list filters/sorts/paging, statistics
/// placeholders, and nested-write rollback. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class RecipeDocumentIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string RecipesPath = "/api/v1/recipes";
    private const string IngredientsPath = "/api/v1/ingredients";
    private const string UnitsPath = "/api/v1/units";
    private const string ProteinsPath = "/api/v1/proteins";
    private const string TagsPath = "/api/v1/tags";
    private const string AdminPassword = "root horse battery staple";
    private const string TestUserAgent = "recipe-document-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);
        await ClearLibraryAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Create_round_trips_groups_ingredients_and_instructions_in_order()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var flour = await SeedIngredientAsync("Flour", cancellationToken: cancellationToken);
        var sugar = await SeedIngredientAsync("Sugar", cancellationToken: cancellationToken);
        var salt = await SeedIngredientAsync("Salt", cancellationToken: cancellationToken);
        var grams = await SeedUnitAsync("g", "gram", UnitDimension.Mass, cancellationToken: cancellationToken);

        var payload = new
        {
            name = "Document Recipe",
            ingredients = new object[]
            {
                new
                {
                    ingredient_id = salt.Id,
                    quantity = (decimal?)null,
                    unit_id = (long?)null,
                    preparation_note = "to taste",
                    is_optional = true,
                },
            },
            ingredient_groups = new object[]
            {
                new
                {
                    title = "Dry",
                    ingredients = new object[]
                    {
                        new { ingredient_id = flour.Id, quantity = 250.0m, unit_id = grams.Id },
                        new { ingredient_id = sugar.Id, quantity = 2.5m, unit_id = (long?)null },
                    },
                },
                new { title = (string?)null, ingredients = Array.Empty<object>() },
            },
            instruction_sections = new object[]
            {
                new { title = "Prep", steps = new[] { "  Chop  ", "Mix." } },
                new { title = (string?)null, steps = Array.Empty<string>() },
            },
        };

        var created = await CreateRecipeAsync(session, payload, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var root = await ReadJsonAsync(created, cancellationToken);
        var id = root.GetProperty("id").GetInt64();

        // Ungrouped line: null quantity + preparation note + optional.
        var ungrouped = root.GetProperty("ingredients");
        ungrouped.GetArrayLength().Should().Be(1);
        ungrouped[0].GetProperty("ingredient").GetProperty("id").GetInt64().Should().Be(salt.Id);
        ungrouped[0].GetProperty("ingredient").GetProperty("name").GetString().Should().Be("Salt");
        ungrouped[0].GetProperty("quantity").ValueKind.Should().Be(JsonValueKind.Null);
        ungrouped[0].GetProperty("unit").ValueKind.Should().Be(JsonValueKind.Null);
        ungrouped[0].GetProperty("preparation_note").GetString().Should().Be("to taste");
        ungrouped[0].GetProperty("is_optional").GetBoolean().Should().BeTrue();
        ungrouped[0].GetProperty("sort_order").GetInt32().Should().Be(0);

        // Groups keep array order, null titles, and per-group ingredient order.
        var groups = root.GetProperty("ingredient_groups");
        groups.GetArrayLength().Should().Be(2);
        groups[0].GetProperty("title").GetString().Should().Be("Dry");
        groups[0].GetProperty("sort_order").GetInt32().Should().Be(0);
        var groupIngredients = groups[0].GetProperty("ingredients");
        groupIngredients.GetArrayLength().Should().Be(2);
        groupIngredients[0].GetProperty("ingredient").GetProperty("id").GetInt64().Should().Be(flour.Id);
        groupIngredients[0].GetProperty("quantity").GetDecimal().Should().Be(250m);
        groupIngredients[0].GetProperty("unit").GetProperty("id").GetInt64().Should().Be(grams.Id);
        groupIngredients[0].GetProperty("unit").GetProperty("symbol").GetString().Should().Be("g");
        groupIngredients[1].GetProperty("ingredient").GetProperty("id").GetInt64().Should().Be(sugar.Id);
        groupIngredients[1].GetProperty("unit").ValueKind.Should().Be(JsonValueKind.Null);
        groups[1].GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null);
        groups[1].GetProperty("ingredients").GetArrayLength().Should().Be(0);

        // Sections keep order; step whitespace is trimmed; empty sections allowed.
        var sections = root.GetProperty("instruction_sections");
        sections.GetArrayLength().Should().Be(2);
        sections[0].GetProperty("title").GetString().Should().Be("Prep");
        sections[0].GetProperty("steps")[0].GetProperty("text").GetString().Should().Be("Chop");
        sections[0].GetProperty("steps")[1].GetProperty("text").GetString().Should().Be("Mix.");
        sections[1].GetProperty("steps").GetArrayLength().Should().Be(0);

        // Reading the detail document returns the same slices.
        var detail = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}/{id}", cancellationToken),
            cancellationToken);
        detail.GetProperty("ingredient_groups").GetArrayLength().Should().Be(2);
        detail.GetProperty("ingredients").GetArrayLength().Should().Be(1);
        detail.GetProperty("instruction_sections").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Create_round_trips_decimal_precision_and_quantity_bounds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var ingredient = await SeedIngredientAsync("Precision", cancellationToken: cancellationToken);

        var payload = new
        {
            name = "Precision Recipe",
            ingredients = new object[]
            {
                new { ingredient_id = ingredient.Id, quantity = 0.12345678901234m },
                new { ingredient_id = ingredient.Id, quantity = 1000000m },
                new { ingredient_id = ingredient.Id, quantity = 0m },
            },
        };

        var created = await CreateRecipeAsync(session, payload, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var root = await ReadJsonAsync(created, cancellationToken);
        var items = root.GetProperty("ingredients");
        items[0].GetProperty("quantity").GetDecimal().Should().Be(0.12345678901234m);
        items[1].GetProperty("quantity").GetDecimal().Should().Be(1000000m);

        var tooBig = await CreateRecipeAsync(session, new
        {
            name = "Too Big",
            ingredients = new object[] { new { ingredient_id = ingredient.Id, quantity = 1000000.1m } },
        }, cancellationToken);
        tooBig.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(tooBig, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("ingredients[0].quantity", out _).Should().BeTrue();

        var tooPrecise = await CreateRecipeAsync(session, new
        {
            name = "Too Precise",
            ingredients = new object[]
            {
                new { ingredient_id = ingredient.Id, quantity = 0.123456789012345m },
            },
        }, cancellationToken);
        tooPrecise.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Protein_and_tag_associations_deduplicate_and_replace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var chicken = await SeedProteinAsync("Chicken", cancellationToken: cancellationToken);
        var beef = await SeedProteinAsync("Beef", cancellationToken: cancellationToken);
        var weeknight = await SeedTagAsync("Weeknight", cancellationToken: cancellationToken);
        var quick = await SeedTagAsync("Quick", cancellationToken: cancellationToken);

        var created = await CreateRecipeAsync(session, new
        {
            name = "Linked Recipe",
            protein_ids = new[] { chicken.Id, chicken.Id, beef.Id },
            tag_ids = new[] { weeknight.Id, weeknight.Id, quick.Id },
        }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var root = await ReadJsonAsync(created, cancellationToken);
        var id = root.GetProperty("id").GetInt64();
        root.GetProperty("proteins").GetArrayLength().Should().Be(2);
        root.GetProperty("tags").GetArrayLength().Should().Be(2);

        var replaced = await PatchRecipeAsync(session, id, new
        {
            protein_ids = new[] { beef.Id },
            tag_ids = Array.Empty<long>(),
        }, cancellationToken);
        replaced.StatusCode.Should().Be(HttpStatusCode.OK);
        var replacedRoot = await ReadJsonAsync(replaced, cancellationToken);
        replacedRoot.GetProperty("proteins").GetArrayLength().Should().Be(1);
        replacedRoot.GetProperty("proteins")[0].GetProperty("id").GetInt64().Should().Be(beef.Id);
        replacedRoot.GetProperty("tags").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Retired_references_cannot_be_newly_selected_but_existing_ones_survive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var active = await SeedIngredientAsync("Active Ingredient", cancellationToken: cancellationToken);
        var retired = await SeedIngredientAsync("Retired Ingredient", isActive: false, cancellationToken: cancellationToken);
        var activeProtein = await SeedProteinAsync("Active Protein", cancellationToken: cancellationToken);
        var retiredProtein = await SeedProteinAsync("Retired Protein", isActive: false, cancellationToken: cancellationToken);

        // Newly selecting a retired ingredient is rejected.
        var rejected = await CreateRecipeAsync(session, new
        {
            name = "Rejected Recipe",
            ingredients = new object[] { new { ingredient_id = retired.Id, quantity = 1m } },
        }, cancellationToken);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountRecipesAsync(cancellationToken)).Should().Be(0);

        // Newly selecting a retired protein is rejected.
        var rejectedProtein = await CreateRecipeAsync(session, new
        {
            name = "Rejected Protein",
            protein_ids = new[] { retiredProtein.Id },
        }, cancellationToken);
        rejectedProtein.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Keep an active reference, then retire it; the existing link stays valid.
        var created = await CreateRecipeAsync(session, new
        {
            name = "Retained Recipe",
            ingredients = new object[] { new { ingredient_id = active.Id, quantity = 1m } },
            protein_ids = new[] { activeProtein.Id },
        }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        await SetIngredientActiveAsync(active.Id, false, cancellationToken);
        await SetProteinActiveAsync(activeProtein.Id, false, cancellationToken);

        var kept = await PatchRecipeAsync(session, id, new
        {
            ingredients = new object[] { new { ingredient_id = active.Id, quantity = 2m } },
            protein_ids = new[] { activeProtein.Id },
        }, cancellationToken);
        kept.StatusCode.Should().Be(HttpStatusCode.OK);
        var keptRoot = await ReadJsonAsync(kept, cancellationToken);
        keptRoot.GetProperty("ingredients")[0].GetProperty("ingredient").GetProperty("is_active")
            .GetBoolean().Should().BeFalse("the retained retired reference is displayed as inactive");
        keptRoot.GetProperty("proteins")[0].GetProperty("is_active").GetBoolean().Should().BeFalse();

        // Adding a different retired reference is still rejected.
        var retiredUnit = await SeedUnitAsync(
            "legacycup",
            "legacy cup",
            UnitDimension.Nonconvertible,
            isActive: false,
            cancellationToken: cancellationToken);
        var addRetiredUnit = await PatchRecipeAsync(session, id, new
        {
            ingredients = new object[] { new { ingredient_id = active.Id, quantity = 2m, unit_id = retiredUnit.Id } },
        }, cancellationToken);
        addRetiredUnit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Recipe_references_block_reference_data_hard_deletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var ingredient = await SeedIngredientAsync("Guarded Ingredient", cancellationToken: cancellationToken);
        var unit = await SeedUnitAsync("guardunit", "guard unit", UnitDimension.Count, cancellationToken: cancellationToken);
        var protein = await SeedProteinAsync("Guarded Protein", cancellationToken: cancellationToken);
        var tag = await SeedTagAsync("Guarded Tag", cancellationToken: cancellationToken);

        var created = await CreateRecipeAsync(session, new
        {
            name = "Guarded Recipe",
            ingredients = new object[] { new { ingredient_id = ingredient.Id, quantity = 1m, unit_id = unit.Id } },
            protein_ids = new[] { protein.Id },
            tag_ids = new[] { tag.Id },
        }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        (await DeleteAsync(session, $"{IngredientsPath}/{ingredient.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await DeleteAsync(session, $"{UnitsPath}/{unit.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await DeleteAsync(session, $"{ProteinsPath}/{protein.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        var tagDelete = await DeleteAsync(session, $"{TagsPath}/{tag.Id}", cancellationToken);
        tagDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(tagDelete, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.TagInUse);
    }

    [Fact]
    public async Task Nested_validation_and_step_rules_reject_the_whole_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var ingredient = await SeedIngredientAsync("Rollback Ingredient", cancellationToken: cancellationToken);

        var badIngredient = await CreateRecipeAsync(session, new
        {
            name = "Bad Ingredient",
            ingredients = new object[]
            {
                new { ingredient_id = ingredient.Id, quantity = 1m },
                new { ingredient_id = 999999L, quantity = 1m },
            },
            instruction_sections = new object[]
            {
                new { title = "Steps", steps = new[] { "Only step" } },
            },
        }, cancellationToken);
        badIngredient.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountRecipesAsync(cancellationToken)).Should().Be(0, "no partial recipe row");
        (await CountTableAsync("recipe_ingredients", cancellationToken)).Should().Be(0);
        (await CountTableAsync("recipe_instruction_steps", cancellationToken)).Should().Be(0);
        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();

        var blankStep = await CreateRecipeAsync(session, new
        {
            name = "Blank Step",
            instruction_sections = new object[]
            {
                new { title = "Steps", steps = new[] { "   " } },
            },
        }, cancellationToken);
        blankStep.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountRecipesAsync(cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Update_replaces_only_the_slices_that_are_present()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var ingredient = await SeedIngredientAsync("Slice Ingredient", cancellationToken: cancellationToken);

        var created = await CreateRecipeAsync(session, new
        {
            name = "Slice Recipe",
            ingredients = new object[] { new { ingredient_id = ingredient.Id, quantity = 1m } },
            ingredient_groups = new object[]
            {
                new { title = "Group A", ingredients = new object[] { new { ingredient_id = ingredient.Id, quantity = 2m } } },
            },
            instruction_sections = new object[]
            {
                new { title = "First", steps = new[] { "One" } },
            },
        }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        // Replacing only the groups leaves the ungrouped ingredient and the
        // instructions untouched.
        var groupOnly = await PatchRecipeAsync(session, id, new
        {
            ingredient_groups = new object[]
            {
                new { title = "Group B", ingredients = new object[] { new { ingredient_id = ingredient.Id, quantity = 3m } } },
            },
        }, cancellationToken);
        groupOnly.StatusCode.Should().Be(HttpStatusCode.OK);
        var groupRoot = await ReadJsonAsync(groupOnly, cancellationToken);
        groupRoot.GetProperty("ingredient_groups")[0].GetProperty("title").GetString().Should().Be("Group B");
        groupRoot.GetProperty("ingredients").GetArrayLength().Should().Be(1, "ungrouped lines are untouched");
        groupRoot.GetProperty("instruction_sections").GetArrayLength().Should().Be(1);

        // Clearing groups is possible; the ungrouped line remains.
        var cleared = await PatchRecipeAsync(session, id, new { ingredient_groups = Array.Empty<object>() }, cancellationToken);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        var clearedRoot = await ReadJsonAsync(cleared, cancellationToken);
        clearedRoot.GetProperty("ingredient_groups").GetArrayLength().Should().Be(0);
        clearedRoot.GetProperty("ingredients").GetArrayLength().Should().Be(1);

        // Clearing ungrouped ingredients leaves the groups in place.
        var clearedUngrouped = await PatchRecipeAsync(session, id, new { ingredients = Array.Empty<object>() }, cancellationToken);
        clearedUngrouped.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(clearedUngrouped, cancellationToken))
            .GetProperty("ingredients").GetArrayLength().Should().Be(0);

        // Clearing instruction sections removes them.
        var clearedInstructions = await PatchRecipeAsync(
            session,
            id,
            new { instruction_sections = Array.Empty<object>() },
            cancellationToken);
        clearedInstructions.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(clearedInstructions, cancellationToken))
            .GetProperty("instruction_sections").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task List_filters_sorts_and_paging_come_from_the_server()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var chicken = await SeedProteinAsync("Chicken", cancellationToken: cancellationToken);
        var beef = await SeedProteinAsync("Beef", cancellationToken: cancellationToken);
        var tag = await SeedTagAsync("Dinner", cancellationToken: cancellationToken);

        await CreateRecipeAsync(session, new
        {
            name = "Alpha Chicken",
            is_favorite = true,
            protein_ids = new[] { chicken.Id },
            tag_ids = new[] { tag.Id },
        }, cancellationToken);
        await CreateRecipeAsync(session, new
        {
            name = "Beta Beef",
            status = "manual_only",
            protein_ids = new[] { beef.Id },
        }, cancellationToken);
        await CreateRecipeAsync(session, new
        {
            name = "Gamma Retired",
            status = "retired",
            protein_ids = new[] { chicken.Id },
        }, cancellationToken);

        var search = await ListAsync(session, "?search=chicken", cancellationToken);
        search.GetProperty("total").GetInt32().Should().Be(1);
        search.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Alpha Chicken");

        var status = await ListAsync(session, "?status=retired", cancellationToken);
        status.GetProperty("total").GetInt32().Should().Be(1);

        var favorite = await ListAsync(session, "?favorite=true", cancellationToken);
        favorite.GetProperty("total").GetInt32().Should().Be(1);

        var byProtein = await ListAsync(session, $"?protein_id={chicken.Id}", cancellationToken);
        byProtein.GetProperty("total").GetInt32().Should().Be(2);

        var byTag = await ListAsync(session, $"?tag_id={tag.Id}", cancellationToken);
        byTag.GetProperty("total").GetInt32().Should().Be(1);

        var byName = await ListAsync(session, "?sort=name", cancellationToken);
        var names = byName.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString()).ToArray();
        names.Should().Equal("Alpha Chicken", "Beta Beef", "Gamma Retired");

        var byFavorites = await ListAsync(session, "?sort=favorites", cancellationToken);
        byFavorites.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Alpha Chicken");

        var paged = await ListAsync(session, "?limit=1&offset=1", cancellationToken);
        paged.GetProperty("total").GetInt32().Should().Be(3);
        paged.GetProperty("items").GetArrayLength().Should().Be(1);

        // Metrics are always present; with no calendar history they stay null
        // (never a fake zero).
        var alpha = await ReadJsonAsync(
            await session.Client.GetAsync(RecipesPath, cancellationToken),
            cancellationToken);
        var alphaItem = alpha.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "Alpha Chicken");
        alphaItem.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        alphaItem.GetProperty("next_planned").ValueKind.Should().Be(JsonValueKind.Null);
        alphaItem.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);

        var items = alpha.GetProperty("items");
        var firstId = items[0].GetProperty("id").GetInt64();
        var detail = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}/{firstId}", cancellationToken),
            cancellationToken);
        var statistics = detail.GetProperty("statistics");
        statistics.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        statistics.GetProperty("next_planned").ValueKind.Should().Be(JsonValueKind.Null);
        statistics.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Update_with_a_bad_nested_slice_leaves_the_recipe_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var ingredient = await SeedIngredientAsync("Stable Ingredient", cancellationToken: cancellationToken);

        var created = await CreateRecipeAsync(session, new
        {
            name = "Stable Recipe",
            ingredients = new object[] { new { ingredient_id = ingredient.Id, quantity = 1m } },
        }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        var rejected = await PatchRecipeAsync(session, id, new
        {
            ingredients = new object[]
            {
                new { ingredient_id = ingredient.Id, quantity = 2m },
                new { ingredient_id = 999999L, quantity = 1m },
            },
        }, cancellationToken);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var detail = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}/{id}", cancellationToken),
            cancellationToken);
        detail.GetProperty("ingredients").GetArrayLength().Should().Be(1);
        detail.GetProperty("ingredients")[0].GetProperty("quantity").GetDecimal().Should().Be(1m);
        (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle()
            .Which.EventType.Should().Be(AuditEventTypes.Created, "a rejected patch writes no audit row");
    }

    [Fact]
    public async Task Audit_after_snapshot_includes_the_nested_slices()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (factory, session) = await PrepareAsync(cancellationToken);
        using var factoryScope = factory;
        using var sessionScope = session;
        var salt = await SeedIngredientAsync("Audit Salt", cancellationToken: cancellationToken);
        var chicken = await SeedProteinAsync("Audit Chicken", cancellationToken: cancellationToken);
        var tag = await SeedTagAsync("Audit Tag", cancellationToken: cancellationToken);

        var created = await CreateRecipeAsync(session, new
        {
            name = "Audit Recipe",
            ingredients = new object[] { new { ingredient_id = salt.Id, quantity = 1m } },
            ingredient_groups = new object[]
            {
                new
                {
                    title = "Group",
                    ingredients = new object[] { new { ingredient_id = salt.Id, quantity = 2m } },
                },
            },
            instruction_sections = new object[]
            {
                new { title = "Steps", steps = new[] { "Audit step" } },
            },
            protein_ids = new[] { chicken.Id },
            tag_ids = new[] { tag.Id },
        }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var audit = (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle().Subject;
        audit.EventType.Should().Be(AuditEventTypes.Created);
        var after = JsonNode.Parse(audit.AfterJson!)!;
        after["ingredients"]!.AsArray().Should().HaveCount(1, "the after-image records the new ingredient");
        after["ingredients"]![0]!["ingredient_id"]!.GetValue<long>().Should().Be(salt.Id);
        after["ingredient_groups"]!.AsArray().Should().HaveCount(1);
        after["ingredient_groups"]![0]!["ingredients"]!.AsArray().Should().HaveCount(1);
        after["instruction_sections"]!.AsArray().Should().HaveCount(1);
        after["instruction_sections"]![0]!["steps"]!.AsArray().Should().HaveCount(1);
        after["proteins"]!.AsArray().Should().HaveCount(1);
        after["tags"]!.AsArray().Should().HaveCount(1);
    }

    private async Task<(ApiFactory Factory, SignedInClient Session)> PrepareAsync(CancellationToken cancellationToken)
    {
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var factory = fixture.CreateFactory();

        // Force startup so the idempotent reference-data seeder runs (it seeds
        // default departments, units, ingredients, proteins, and tags), then
        // clear the vocabulary so each test controls its own reference data.
        using (factory.CreateClient())
        {
        }

        await ClearLibraryAsync(cancellationToken);
        var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        return (factory, session);
    }

    /// <summary>Clears every recipe and reference-data row (children first).</summary>
    private async Task ClearLibraryAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM recipe_tags;
            DELETE FROM recipe_proteins;
            DELETE FROM recipe_instruction_steps;
            DELETE FROM recipe_instruction_sections;
            DELETE FROM recipe_ingredients;
            DELETE FROM recipe_ingredient_groups;
            DELETE FROM recipe_images;
            DELETE FROM recipes;
            DELETE FROM unit_aliases;
            DELETE FROM units;
            DELETE FROM ingredient_aliases;
            DELETE FROM ingredients;
            DELETE FROM grocery_departments;
            DELETE FROM proteins;
            DELETE FROM tags;
            """,
            cancellationToken);
    }

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private static async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password,
        CancellationToken cancellationToken)
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

        var login = await client.SendAsync(request, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        return new SignedInClient(
            client,
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? payload,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static Task<HttpResponseMessage> CreateRecipeAsync(
        SignedInClient session,
        object payload,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session.Client, HttpMethod.Post, RecipesPath, payload, session.CsrfToken, cancellationToken);

    private static Task<HttpResponseMessage> PatchRecipeAsync(
        SignedInClient session,
        long id,
        object payload,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{RecipesPath}/{id}",
            payload,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> DeleteAsync(
        SignedInClient session,
        string path,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session.Client, HttpMethod.Delete, path, payload: null, session.CsrfToken, cancellationToken);

    private static async Task<JsonElement> ListAsync(
        SignedInClient session,
        string query,
        CancellationToken cancellationToken) =>
        await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}{query}", cancellationToken),
            cancellationToken);

    private async Task<Ingredient> SeedIngredientAsync(
        string canonicalName,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(canonicalName)!;
        await using var context = fixture.CreateContext();
        var ingredient = new Ingredient
        {
            CanonicalName = normalized.Display,
            NormalizedName = normalized.Normalized,
            IsActive = isActive,
        };
        context.Ingredients.Add(ingredient);
        await context.SaveChangesAsync(cancellationToken);
        return ingredient;
    }

    private async Task<Unit> SeedUnitAsync(
        string symbol,
        string displayName,
        UnitDimension dimension,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(displayName)!;
        await using var context = fixture.CreateContext();
        var unit = new Unit
        {
            Symbol = symbol,
            DisplayName = displayName,
            NormalizedName = normalized.Normalized,
            Dimension = dimension,
            BaseFactor = dimension == UnitDimension.Mass ? 1m : null,
            IsActive = isActive,
        };
        context.Units.Add(unit);
        await context.SaveChangesAsync(cancellationToken);
        return unit;
    }

    private async Task<Protein> SeedProteinAsync(
        string name,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var protein = new Protein
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            IsActive = isActive,
        };
        context.Proteins.Add(protein);
        await context.SaveChangesAsync(cancellationToken);
        return protein;
    }

    private async Task<Tag> SeedTagAsync(string name, CancellationToken cancellationToken = default)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var tag = new Tag
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
        };
        context.Tags.Add(tag);
        await context.SaveChangesAsync(cancellationToken);
        return tag;
    }

    private async Task SetIngredientActiveAsync(long id, bool isActive, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Ingredients.Where(ingredient => ingredient.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(ingredient => ingredient.IsActive, isActive),
                cancellationToken);
    }

    private async Task SetProteinActiveAsync(long id, bool isActive, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Proteins.Where(protein => protein.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(protein => protein.IsActive, isActive),
                cancellationToken);
    }

    private async Task<int> CountRecipesAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Recipes.CountAsync(cancellationToken);
    }

    private async Task<int> CountTableAsync(string table, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // The table name is a test-controlled constant, never user input.
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents.AsNoTracking()
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Recipe Document Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkSetupCompletedAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }

    private async Task<User> SeedUserAsync(
        string username,
        string password,
        CancellationToken cancellationToken,
        UserRole role = UserRole.Member)
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
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        (await ReadJsonAsync(response, cancellationToken)).GetProperty("error");
}
