using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
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
/// HTTP-level verification of the M3-02 recipe base metadata and status
/// lifecycle (docs/M3_PLAN.md §1.2–§1.4, §1.17–§1.19): create/read/update,
/// name and text validation, source-URL normalization, minutes, status
/// transitions, list paging, the complete route permission matrix, and audit
/// coverage for every write. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class RecipeEndpointsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string RecipesPath = "/api/v1/recipes";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "recipe-endpoints-test";

    // Built from char codes so the file stays free of escape sequences.
    private static readonly string LineFeed = ((char)10).ToString();

    private static readonly string Tab = ((char)9).ToString();

    private static readonly string ControlCharacter = ((char)1).ToString();

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await fixture.ResetAsync(cancellationToken);

        // M1SetupFixture.ResetAsync already clears the M3 recipe tables; the
        // explicit delete here keeps this suite independent of execution order
        // within the collection.
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
            """,
            cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Create_returns_the_default_document_shape_and_one_created_audit_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await CreateRecipeAsync(session, """{"name":"  Ratatouille  "}""", cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        id.Should().BeGreaterThan(0);
        createdRoot.GetProperty("name").GetString().Should().Be("Ratatouille");

        foreach (var optional in new[]
                 {
                     "description", "source_site", "source_url", "author",
                     "prep_minutes", "cook_minutes", "notes",
                 })
        {
            createdRoot.GetProperty(optional).ValueKind.Should().Be(
                JsonValueKind.Null,
                "'{0}' defaults to null",
                optional);
        }

        createdRoot.GetProperty("status").GetString().Should().Be("normal");
        createdRoot.GetProperty("is_favorite").GetBoolean().Should().BeFalse();
        createdRoot.GetProperty("is_vegan").GetBoolean().Should().BeFalse();
        createdRoot.GetProperty("is_vegetarian").GetBoolean().Should().BeFalse();
        createdRoot.GetProperty("image").ValueKind.Should().Be(JsonValueKind.Null);
        foreach (var collection in new[]
                 {
                     "ingredient_groups", "ingredients", "instruction_sections", "proteins", "tags",
                 })
        {
            createdRoot.GetProperty(collection).GetArrayLength().Should().Be(
                0,
                "'{0}' starts empty",
                collection);
        }

        var createdAt = createdRoot.GetProperty("created_at").GetDateTimeOffset();
        createdRoot.GetProperty("updated_at").GetDateTimeOffset().Should().Be(createdAt);

        var detail = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}/{id}", cancellationToken),
            cancellationToken);
        detail.GetProperty("id").GetInt64().Should().Be(id);
        detail.GetProperty("name").GetString().Should().Be("Ratatouille");
        detail.GetProperty("status").GetString().Should().Be("normal");
        detail.GetProperty("image").ValueKind.Should().Be(JsonValueKind.Null);
        detail.GetProperty("tags").GetArrayLength().Should().Be(0);
        detail.GetProperty("prep_minutes").ValueKind.Should().Be(JsonValueKind.Null);

        var list = await ReadJsonAsync(
            await session.Client.GetAsync(RecipesPath, cancellationToken),
            cancellationToken);
        list.GetProperty("total").GetInt32().Should().Be(1);
        list.GetProperty("limit").GetInt32().Should().Be(24);
        list.GetProperty("offset").GetInt32().Should().Be(0);
        var item = list.GetProperty("items")[0];
        item.GetProperty("id").GetInt64().Should().Be(id);
        item.GetProperty("name").GetString().Should().Be("Ratatouille");
        item.GetProperty("status").GetString().Should().Be("normal");
        item.GetProperty("is_favorite").GetBoolean().Should().BeFalse();
        item.GetProperty("is_vegan").GetBoolean().Should().BeFalse();
        item.GetProperty("is_vegetarian").GetBoolean().Should().BeFalse();
        item.GetProperty("image").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("last_eaten").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("times_eaten").ValueKind.Should().Be(JsonValueKind.Null);

        var audit = (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle().Subject;
        audit.EventType.Should().Be(AuditEventTypes.Created);
        audit.EntityType.Should().Be(AuditEntityTypes.Recipe);
        audit.EntityId.Should().Be(id.ToString(CultureInfo.InvariantCulture));
        audit.ActorUserId.Should().Be(admin.Id);
        audit.BeforeJson.Should().BeNull();
        var after = Snapshot(audit.AfterJson!);
        after["name"]!.GetValue<string>().Should().Be("Ratatouille");
        after["normalized_name"]!.GetValue<string>().Should().Be("ratatouille");
        after["status"]!.GetValue<string>().Should().Be("normal");
    }

    [Fact]
    public async Task Create_round_trips_every_base_field_and_minutes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            name = "Full Metadata Recipe",
            description = "A complete description.",
            source_site = "Example Kitchen",
            source_url = "https://example.com/recipes/full?portion=2",
            author = "Chef Example",
            prep_minutes = 15,
            cook_minutes = 45,
            notes = "First line." + LineFeed + "Second line.",
            status = "manual_only",
            is_favorite = true,
            is_vegan = false,
            is_vegetarian = true,
        });

        var created = await CreateRecipeAsync(session, payload, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var root = await ReadJsonAsync(created, cancellationToken);
        var id = root.GetProperty("id").GetInt64();
        root.GetProperty("name").GetString().Should().Be("Full Metadata Recipe");
        root.GetProperty("description").GetString().Should().Be("A complete description.");
        root.GetProperty("source_site").GetString().Should().Be("Example Kitchen");
        root.GetProperty("source_url").GetString().Should().Be("https://example.com/recipes/full?portion=2");
        root.GetProperty("author").GetString().Should().Be("Chef Example");
        root.GetProperty("prep_minutes").GetInt32().Should().Be(15);
        root.GetProperty("cook_minutes").GetInt32().Should().Be(45);
        root.GetProperty("notes").GetString().Should().Be("First line." + LineFeed + "Second line.");
        root.GetProperty("status").GetString().Should().Be("manual_only");
        root.GetProperty("is_favorite").GetBoolean().Should().BeTrue();
        root.GetProperty("is_vegan").GetBoolean().Should().BeFalse();
        root.GetProperty("is_vegetarian").GetBoolean().Should().BeTrue();

        var detail = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}/{id}", cancellationToken),
            cancellationToken);
        detail.GetProperty("description").GetString().Should().Be("A complete description.");
        detail.GetProperty("prep_minutes").GetInt32().Should().Be(15);
        detail.GetProperty("cook_minutes").GetInt32().Should().Be(45);
        detail.GetProperty("status").GetString().Should().Be("manual_only");

        var stored = await ReadStoredRecipeAsync(id, cancellationToken);
        stored.Description.Should().Be("A complete description.");
        stored.SourceSite.Should().Be("Example Kitchen");
        stored.SourceUrl.Should().Be("https://example.com/recipes/full?portion=2");
        stored.Author.Should().Be("Chef Example");
        stored.PrepMinutes.Should().Be(15);
        stored.CookMinutes.Should().Be(45);
        stored.Status.Should().Be(RecipeStatus.ManualOnly);
        stored.IsFavorite.Should().BeTrue();
        stored.IsVegan.Should().BeFalse();
        stored.IsVegetarian.Should().BeTrue();
        stored.NormalizedSourceUrl.Should().Be("https://example.com/recipes/full?portion=2");
    }

    [Fact]
    public async Task Create_rejects_normalized_name_duplicates_with_the_existing_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var original = await CreateRecipeAsync(session, """{"name":"Pómmes"}""", cancellationToken);
        original.StatusCode.Should().Be(HttpStatusCode.Created);
        var originalId = (await ReadJsonAsync(original, cancellationToken)).GetProperty("id").GetInt64();

        foreach (var variant in new[] { " pommes ", "POMMES", "PÖMMES", "Pómmes" })
        {
            var conflict = await CreateRecipeAsync(
                session,
                JsonSerializer.Serialize(new { name = variant }),
                cancellationToken);
            conflict.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                "'{0}' normalizes to the existing name",
                variant);
            var error = await ReadErrorAsync(conflict, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.RecipeExists);
            error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(originalId);
        }

        (await CountRecipesAsync(cancellationToken)).Should().Be(1);
        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.EventType.Should().Be(AuditEventTypes.Created);
    }

    [Fact]
    public async Task Create_validates_name_presence_type_length_and_control_characters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var invalid = new (string Json, string Field)[]
        {
            ("""{}""", "name"),
            ("""{"name":5}""", "name"),
            ("""{"name":null}""", "name"),
            ("""{"name":"   "}""", "name"),
            ("""{"name":""}""", "name"),
            (JsonSerializer.Serialize(new { name = "Bad" + ControlCharacter + "Name" }), "name"),
            (JsonSerializer.Serialize(new { name = new string('x', RecipeService.MaxNameLength + 1) }), "name"),
        };

        foreach (var (json, field) in invalid)
        {
            var response = await CreateRecipeAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must fail", json);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty(field, out _)
                .Should().BeTrue("the response identifies the '{0}' field", field);
        }

        (await CountRecipesAsync(cancellationToken)).Should().Be(0);
        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();

        var boundary = await CreateRecipeAsync(
            session,
            JsonSerializer.Serialize(new { name = new string('a', RecipeService.MaxNameLength) }),
            cancellationToken);
        boundary.StatusCode.Should().Be(HttpStatusCode.Created, "200 characters is the inclusive maximum");
        (await CountRecipesAsync(cancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Create_validates_long_text_limits_and_control_characters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var accepted = await CreateRecipeAsync(
            session,
            JsonSerializer.Serialize(new
            {
                name = "Long Text Recipe",
                description = "Step one." + LineFeed + "Step two." + Tab + "Tabbed.",
                notes = "  Notes with" + LineFeed + "newline  ",
            }),
            cancellationToken);
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        var acceptedRoot = await ReadJsonAsync(accepted, cancellationToken);
        acceptedRoot.GetProperty("description").GetString()
            .Should().Be("Step one." + LineFeed + "Step two." + Tab + "Tabbed.");
        acceptedRoot.GetProperty("notes").GetString().Should().Be("Notes with" + LineFeed + "newline");

        var invalid = new (string Json, string Field)[]
        {
            (JsonSerializer.Serialize(new { name = "Long Description", description = new string('d', RecipeService.MaxLongTextLength + 1) }), "description"),
            (JsonSerializer.Serialize(new { name = "Long Notes", notes = new string('n', RecipeService.MaxLongTextLength + 1) }), "notes"),
            (JsonSerializer.Serialize(new { name = "Long Source Site", source_site = new string('s', RecipeService.MaxSourceSiteLength + 1) }), "source_site"),
            (JsonSerializer.Serialize(new { name = "Long Author", author = new string('a', RecipeService.MaxAuthorLength + 1) }), "author"),
            (JsonSerializer.Serialize(new { name = "Bad Description", description = "bad" + ControlCharacter + "value" }), "description"),
            (JsonSerializer.Serialize(new { name = "Bad Notes", notes = "bad" + ControlCharacter + "value" }), "notes"),
            (JsonSerializer.Serialize(new { name = "Bad Source Site", source_site = "bad" + ControlCharacter + "value" }), "source_site"),
        };

        foreach (var (json, field) in invalid)
        {
            var response = await CreateRecipeAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the payload must be rejected");
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty(field, out _)
                .Should().BeTrue("the response identifies the '{0}' field", field);
        }

        var exact = await CreateRecipeAsync(
            session,
            JsonSerializer.Serialize(new
            {
                name = "Exact Long Text",
                description = new string('e', RecipeService.MaxLongTextLength),
            }),
            cancellationToken);
        exact.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "20,000 characters is the inclusive maximum");
        (await ReadJsonAsync(exact, cancellationToken)).GetProperty("description").GetString()!
            .Length.Should().Be(RecipeService.MaxLongTextLength);

        (await CountRecipesAsync(cancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Create_normalizes_source_urls_and_rejects_unsupported_targets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var first = await CreateRecipeAsync(
            session,
            """{"name":"Url One","source_url":"https://Example.COM:443/Path?q=1a#section"}""",
            cancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstRoot = await ReadJsonAsync(first, cancellationToken);
        var firstId = firstRoot.GetProperty("id").GetInt64();
        firstRoot.GetProperty("source_url").GetString()
            .Should().Be("https://Example.COM:443/Path?q=1a#section", "the raw URL is preserved");
        (await ReadStoredRecipeAsync(firstId, cancellationToken)).NormalizedSourceUrl
            .Should().Be("https://example.com/Path?q=1a");

        var second = await CreateRecipeAsync(
            session,
            """{"name":"Url Two","source_url":"HTTP://Example.com:80/Other"}""",
            cancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondId = (await ReadJsonAsync(second, cancellationToken)).GetProperty("id").GetInt64();
        (await ReadStoredRecipeAsync(secondId, cancellationToken)).NormalizedSourceUrl
            .Should().Be("http://example.com/Other");

        var third = await CreateRecipeAsync(
            session,
            """{"name":"Url Three","source_url":"https://Example.com:8443/x#y"}""",
            cancellationToken);
        third.StatusCode.Should().Be(HttpStatusCode.Created);
        var thirdId = (await ReadJsonAsync(third, cancellationToken)).GetProperty("id").GetInt64();
        (await ReadStoredRecipeAsync(thirdId, cancellationToken)).NormalizedSourceUrl
            .Should().Be("https://example.com:8443/x", "a non-default port is kept");

        var invalid = new[]
        {
            """{"name":"Ftp Url","source_url":"ftp://example.com/recipes"}""",
            """{"name":"Relative Url","source_url":"recipes/1"}""",
            """{"name":"Words Url","source_url":"not a url"}""",
            """{"name":"Empty Host","source_url":"https://"}""",
            """{"name":"Control Url","source_url":"https://example.com/a\nb"}""",
            JsonSerializer.Serialize(new
            {
                name = "Overlong Url",
                source_url = "https://example.com/" + new string('a', RecipeService.MaxSourceUrlLength),
            }),
        };

        foreach (var json in invalid)
        {
            var response = await CreateRecipeAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must be rejected", json);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty("source_url", out _)
                .Should().BeTrue("the response identifies the 'source_url' field");
        }

        (await CountRecipesAsync(cancellationToken)).Should().Be(3);
    }

    [Fact]
    public async Task Create_validates_minutes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var boundary = await CreateRecipeAsync(
            session,
            """{"name":"Minute Boundary","prep_minutes":0,"cook_minutes":1440}""",
            cancellationToken);
        boundary.StatusCode.Should().Be(HttpStatusCode.Created);
        var boundaryRoot = await ReadJsonAsync(boundary, cancellationToken);
        boundaryRoot.GetProperty("prep_minutes").GetInt32().Should().Be(0);
        boundaryRoot.GetProperty("cook_minutes").GetInt32().Should().Be(1440);

        var cleared = await CreateRecipeAsync(
            session,
            """{"name":"Minute Nulls","prep_minutes":null,"cook_minutes":null}""",
            cancellationToken);
        cleared.StatusCode.Should().Be(HttpStatusCode.Created);
        var clearedRoot = await ReadJsonAsync(cleared, cancellationToken);
        clearedRoot.GetProperty("prep_minutes").ValueKind.Should().Be(JsonValueKind.Null);
        clearedRoot.GetProperty("cook_minutes").ValueKind.Should().Be(JsonValueKind.Null);

        var invalid = new (string Json, string Field)[]
        {
            ("""{"name":"Negative Minutes","prep_minutes":-1}""", "prep_minutes"),
            ("""{"name":"Too Many Minutes","cook_minutes":1441}""", "cook_minutes"),
            ("""{"name":"Fractional Minutes","prep_minutes":12.5}""", "prep_minutes"),
            ("""{"name":"String Minutes","cook_minutes":"10"}""", "cook_minutes"),
        };

        foreach (var (json, field) in invalid)
        {
            var response = await CreateRecipeAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must be rejected", json);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty(field, out _)
                .Should().BeTrue("the response identifies the '{0}' field", field);
        }
    }

    [Fact]
    public async Task Create_round_trips_all_three_status_values_and_rejects_unknown_ones()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        foreach (var status in new[] { "normal", "manual_only", "retired" })
        {
            var created = await CreateRecipeAsync(
                session,
                JsonSerializer.Serialize(new { name = $"Status {status}", status }),
                cancellationToken);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var root = await ReadJsonAsync(created, cancellationToken);
            root.GetProperty("status").GetString().Should().Be(status);
            var id = root.GetProperty("id").GetInt64();
            (await ReadJsonAsync(
                    await session.Client.GetAsync($"{RecipesPath}/{id}", cancellationToken),
                    cancellationToken))
                .GetProperty("status").GetString().Should().Be(status);
        }

        var invalid = new (string Json, string Field)[]
        {
            ("""{"name":"Bad Status One","status":"someday"}""", "status"),
            ("""{"name":"Bad Status Two","status":"Normal"}""", "status"),
            ("""{"name":"Bad Status Three","status":""}""", "status"),
            ("""{"name":"Bad Status Four","status":5}""", "status"),
            ("""{"name":"Bad Status Five","status":null}""", "status"),
        };

        foreach (var (json, field) in invalid)
        {
            var response = await CreateRecipeAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must be rejected", json);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty(field, out _)
                .Should().BeTrue("the response identifies the '{0}' field", field);
        }

        (await CountRecipesAsync(cancellationToken)).Should().Be(3);
    }

    [Fact]
    public async Task Patch_applies_partial_updates_clears_optionals_and_advances_updated_at()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var payload = JsonSerializer.Serialize(new
        {
            name = "Patch Target",
            description = "Before",
            source_site = "Before Site",
            source_url = "https://example.com/before",
            author = "Before Author",
            prep_minutes = 10,
            cook_minutes = 20,
            notes = "Before notes",
        });

        var created = await CreateRecipeAsync(session, payload, cancellationToken);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        var createdAt = createdRoot.GetProperty("created_at").GetDateTimeOffset();
        var initialUpdatedAt = createdRoot.GetProperty("updated_at").GetDateTimeOffset();

        await Task.Delay(25, cancellationToken);
        var cleared = await PatchRecipeAsync(
            session,
            id,
            """{"description":null,"source_site":null,"source_url":null,"author":null,"prep_minutes":null,"cook_minutes":null,"notes":null}""",
            cancellationToken);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        var clearedRoot = await ReadJsonAsync(cleared, cancellationToken);
        foreach (var optional in new[]
                 {
                     "description", "source_site", "source_url", "author",
                     "prep_minutes", "cook_minutes", "notes",
                 })
        {
            clearedRoot.GetProperty(optional).ValueKind.Should().Be(
                JsonValueKind.Null,
                "'{0}' is cleared by an explicit null",
                optional);
        }

        clearedRoot.GetProperty("name").GetString().Should().Be("Patch Target");
        clearedRoot.GetProperty("created_at").GetDateTimeOffset()
            .Should().BeCloseTo(createdAt, TimeSpan.FromMicroseconds(1));
        clearedRoot.GetProperty("updated_at").GetDateTimeOffset().Should().BeAfter(initialUpdatedAt);

        var stored = await ReadStoredRecipeAsync(id, cancellationToken);
        stored.SourceUrl.Should().BeNull();
        stored.NormalizedSourceUrl.Should().BeNull("clearing the URL clears the lookup form too");
        stored.PrepMinutes.Should().BeNull();
        stored.CookMinutes.Should().BeNull();

        await Task.Delay(25, cancellationToken);
        var partial = await PatchRecipeAsync(
            session,
            id,
            """{"name":"Renamed Patch Target","prep_minutes":5}""",
            cancellationToken);
        partial.StatusCode.Should().Be(HttpStatusCode.OK);
        var partialRoot = await ReadJsonAsync(partial, cancellationToken);
        partialRoot.GetProperty("name").GetString().Should().Be("Renamed Patch Target");
        partialRoot.GetProperty("prep_minutes").GetInt32().Should().Be(5);
        partialRoot.GetProperty("description").ValueKind.Should().Be(
            JsonValueKind.Null,
            "omitted fields stay unchanged");

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(AuditEventTypes.Created, AuditEventTypes.Updated, AuditEventTypes.Updated);
        Snapshot(rows[1].BeforeJson!)["source_url"]!.GetValue<string>()
            .Should().Be("https://example.com/before");
        Snapshot(rows[1].AfterJson!)["source_url"].Should().BeNull();
    }

    [Fact]
    public async Task Patch_rejects_empty_unknown_and_invalid_bodies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await CreateRecipeAsync(session, """{"name":"Validation Target"}""", cancellationToken);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        var updatedAt = createdRoot.GetProperty("updated_at").GetDateTimeOffset();

        var invalid = new (string Json, string Field)[]
        {
            ("""{}""", "body"),
            ("""{"unknown_field":true}""", "body"),
            ("""{"name":null}""", "name"),
            ("""{"name":"   "}""", "name"),
            ("""{"name":5}""", "name"),
            ("""{"status":"someday"}""", "status"),
            ("""{"prep_minutes":-1}""", "prep_minutes"),
        };

        foreach (var (json, field) in invalid)
        {
            var response = await PatchRecipeAsync(session, id, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must fail", json);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty(field, out _)
                .Should().BeTrue("the response identifies the '{0}' field", field);
        }

        var stored = await ReadStoredRecipeAsync(id, cancellationToken);
        stored.Name.Should().Be("Validation Target");
        stored.Status.Should().Be(RecipeStatus.Normal);
        stored.UpdatedAt.Should().BeCloseTo(updatedAt, TimeSpan.FromMicroseconds(1), "failed patches leave the row untouched");
        (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle()
            .Which.EventType.Should().Be(AuditEventTypes.Created, "failed patches must not write audit rows");
    }

    [Fact]
    public async Task Patch_supports_status_transitions_in_both_directions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await CreateRecipeAsync(session, """{"name":"Lifecycle Stew"}""", cancellationToken);
        var id = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        foreach (var status in new[] { "manual_only", "retired", "normal" })
        {
            var patched = await PatchRecipeAsync(
                session,
                id,
                JsonSerializer.Serialize(new { status }),
                cancellationToken);
            patched.StatusCode.Should().Be(HttpStatusCode.OK);
            (await ReadJsonAsync(patched, cancellationToken)).GetProperty("status").GetString()
                .Should().Be(status);
        }

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.Created,
            AuditEventTypes.Updated,
            AuditEventTypes.Updated,
            AuditEventTypes.Updated);
        Snapshot(rows[1].BeforeJson!)["status"]!.GetValue<string>().Should().Be("normal");
        Snapshot(rows[1].AfterJson!)["status"]!.GetValue<string>().Should().Be("manual_only");
        Snapshot(rows[2].AfterJson!)["status"]!.GetValue<string>().Should().Be("retired");
        Snapshot(rows[3].AfterJson!)["status"]!.GetValue<string>().Should().Be("normal");
        rows.Should().OnlyContain(row => row.EntityType == AuditEntityTypes.Recipe);
    }

    [Fact]
    public async Task Patch_rename_conflicts_return_recipe_exists_without_touching_the_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var first = await CreateRecipeAsync(session, """{"name":"Existing Name"}""", cancellationToken);
        var firstId = (await ReadJsonAsync(first, cancellationToken)).GetProperty("id").GetInt64();
        var second = await CreateRecipeAsync(session, """{"name":"Other Name"}""", cancellationToken);
        var secondId = (await ReadJsonAsync(second, cancellationToken)).GetProperty("id").GetInt64();

        var conflict = await PatchRecipeAsync(session, secondId, """{"name":"  EXISTING NAME  "}""", cancellationToken);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await ReadErrorAsync(conflict, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.RecipeExists);
        error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(firstId);

        (await ReadStoredRecipeAsync(secondId, cancellationToken)).Name.Should().Be("Other Name");
        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().HaveCount(2).And.OnlyContain(row => row.EventType == AuditEventTypes.Created);
    }

    [Fact]
    public async Task Get_unknown_recipe_ids_return_not_found_without_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        const long unknownId = 987654321;
        var detail = await session.Client.GetAsync($"{RecipesPath}/{unknownId}", cancellationToken);
        detail.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(detail, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);

        var update = await PatchRecipeAsync(session, unknownId, """{"name":"Ghost"}""", cancellationToken);
        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(update, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);

        (await CountRecipesAsync(cancellationToken)).Should().Be(0);
        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Get_list_orders_by_created_at_then_id_and_pages_with_total()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var oldest = await SeedRecipeAsync("Order Oldest", SeedTimestamp, cancellationToken);
        var tiedFirst = await SeedRecipeAsync("Order Tie First", SeedTimestamp.AddMinutes(1), cancellationToken);
        var tiedSecond = await SeedRecipeAsync("Order Tie Second", SeedTimestamp.AddMinutes(1), cancellationToken);
        var newest = await SeedRecipeAsync("Order Newest", SeedTimestamp.AddMinutes(2), cancellationToken);

        var all = await ReadJsonAsync(
            await session.Client.GetAsync(RecipesPath, cancellationToken),
            cancellationToken);
        all.GetProperty("items").EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(newest.Id, tiedSecond.Id, tiedFirst.Id, oldest.Id);
        all.GetProperty("total").GetInt32().Should().Be(4);
        all.GetProperty("limit").GetInt32().Should().Be(24);
        all.GetProperty("offset").GetInt32().Should().Be(0);

        var paged = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?limit=2&offset=1", cancellationToken),
            cancellationToken);
        paged.GetProperty("items").EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(tiedSecond.Id, tiedFirst.Id);
        paged.GetProperty("total").GetInt32().Should().Be(4, "total ignores paging");
        paged.GetProperty("limit").GetInt32().Should().Be(2);
        paged.GetProperty("offset").GetInt32().Should().Be(1);

        var beyond = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?offset=10", cancellationToken),
            cancellationToken);
        beyond.GetProperty("items").GetArrayLength().Should().Be(0);
        beyond.GetProperty("total").GetInt32().Should().Be(4);
        beyond.GetProperty("offset").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task Get_list_defaults_to_24_clamps_to_100_and_rejects_invalid_paging()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        await SeedRecipeRangeAsync(101, cancellationToken);

        var defaults = await ReadJsonAsync(
            await session.Client.GetAsync(RecipesPath, cancellationToken),
            cancellationToken);
        defaults.GetProperty("items").GetArrayLength().Should().Be(24, "limit defaults to 24");
        defaults.GetProperty("total").GetInt32().Should().Be(101);
        defaults.GetProperty("limit").GetInt32().Should().Be(24);
        defaults.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Bulk 101");

        var clamped = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?limit=1000", cancellationToken),
            cancellationToken);
        clamped.GetProperty("items").GetArrayLength().Should().Be(100, "the limit is clamped to 100");
        clamped.GetProperty("limit").GetInt32().Should().Be(100);

        var explicit100 = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?limit=100", cancellationToken),
            cancellationToken);
        explicit100.GetProperty("items").GetArrayLength().Should().Be(100);

        foreach (var query in new[] { "?limit=0", "?limit=-1", "?limit=banana", "?limit=1.5" })
        {
            var invalid = await session.Client.GetAsync($"{RecipesPath}{query}", cancellationToken);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must be rejected", query);
            (await ReadErrorAsync(invalid, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty("limit", out _).Should().BeTrue();
        }

        foreach (var query in new[] { "?offset=-1", "?offset=banana" })
        {
            var invalid = await session.Client.GetAsync($"{RecipesPath}{query}", cancellationToken);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must be rejected", query);
            (await ReadErrorAsync(invalid, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty("offset", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task List_filters_by_favorite_vegan_and_vegetarian()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        await CreateRecipeAsync(
            session,
            """{"name":"Everything","is_favorite":true,"is_vegan":true,"is_vegetarian":true}""",
            cancellationToken);
        await CreateRecipeAsync(
            session,
            """{"name":"Vegetarian Only","is_favorite":true,"is_vegetarian":true}""",
            cancellationToken);
        await CreateRecipeAsync(session, """{"name":"Plain"}""", cancellationToken);

        var favorite = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?favorite=true", cancellationToken),
            cancellationToken);
        favorite.GetProperty("total").GetInt32().Should().Be(2);
        favorite.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .Should().BeEquivalentTo(new[] { "Everything", "Vegetarian Only" });

        var vegan = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?vegan=true", cancellationToken),
            cancellationToken);
        vegan.GetProperty("total").GetInt32().Should().Be(1);
        vegan.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Everything");

        var vegetarian = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?vegetarian=true", cancellationToken),
            cancellationToken);
        vegetarian.GetProperty("total").GetInt32().Should().Be(2);

        var combined = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?favorite=true&vegan=true", cancellationToken),
            cancellationToken);
        combined.GetProperty("total").GetInt32().Should().Be(1);
        combined.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Everything");

        var notFavorite = await ReadJsonAsync(
            await session.Client.GetAsync($"{RecipesPath}?favorite=false", cancellationToken),
            cancellationToken);
        notFavorite.GetProperty("total").GetInt32().Should().Be(1);
        notFavorite.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("Plain");

        foreach (var (query, field) in new[]
                 {
                     ("?favorite=yes", "favorite"),
                     ("?vegan=1", "vegan"),
                     ("?vegetarian=maybe", "vegetarian"),
                 })
        {
            var invalid = await session.Client.GetAsync($"{RecipesPath}{query}", cancellationToken);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must be rejected", query);
            (await ReadErrorAsync(invalid, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty(field, out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Admin_can_use_every_recipe_route()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await CreateRecipeAsync(session, """{"name":"Admin Recipe"}""", cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        (await session.Client.GetAsync(RecipesPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{RecipesPath}/{id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await PatchRecipeAsync(session, id, """{"status":"retired"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Member_with_recipes_edit_can_write_and_reads_through_the_implied_view_grant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var jane = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(jane.Id, PermissionKeys.RecipesEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        var seeded = await SeedRecipeAsync("Edit Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        (await CreateRecipeAsync(session, """{"name":"Member Recipe"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Created);
        (await PatchRecipeAsync(session, seeded.Id, """{"notes":"edited"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // recipes.edit implies recipes.view (PermissionEvaluator, M1-09).
        (await session.Client.GetAsync(RecipesPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{RecipesPath}/{seeded.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Member_with_recipes_view_can_read_but_not_write()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var jane = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(jane.Id, PermissionKeys.RecipesView, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        var seeded = await SeedRecipeAsync("View Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        (await session.Client.GetAsync(RecipesPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{RecipesPath}/{seeded.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var requests = new (HttpMethod Method, string Path, string Json)[]
        {
            (HttpMethod.Post, RecipesPath, """{"name":"Denied Create"}"""),
            (HttpMethod.Patch, $"{RecipesPath}/{seeded.Id}", """{"name":"Denied Patch"}"""),
        };

        foreach (var (method, path, json) in requests)
        {
            var response = await SendJsonAsync(
                session.Client,
                method,
                path,
                json,
                session.CsrfToken,
                cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "{0} {1} requires recipes.edit", method, path);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        (await ReadStoredRecipeAsync(seeded.Id, cancellationToken)).Name.Should().Be("View Target");
    }

    [Fact]
    public async Task Member_without_recipe_grants_is_forbidden_everywhere()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        var seeded = await SeedRecipeAsync("Denied Target", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        var requests = new (HttpMethod Method, string Path, string? Json)[]
        {
            (HttpMethod.Get, RecipesPath, null),
            (HttpMethod.Post, RecipesPath, """{"name":"Denied Create"}"""),
            (HttpMethod.Get, $"{RecipesPath}/{seeded.Id}", null),
            (HttpMethod.Patch, $"{RecipesPath}/{seeded.Id}", """{"name":"Denied Patch"}"""),
        };

        foreach (var (method, path, json) in requests)
        {
            var response = await SendJsonAsync(
                session.Client,
                method,
                path,
                json,
                session.CsrfToken,
                cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "{0} {1}", method, path);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthenticated_recipe_requests_receive_auth_required()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        var seeded = await SeedRecipeAsync("Anonymous Target", SeedTimestamp, cancellationToken);
        using var client = factory.CreateDefaultClient();

        var requests = new (HttpMethod Method, string Path, string? Json)[]
        {
            (HttpMethod.Get, RecipesPath, null),
            (HttpMethod.Post, RecipesPath, """{"name":"Anonymous Create"}"""),
            (HttpMethod.Get, $"{RecipesPath}/{seeded.Id}", null),
            (HttpMethod.Patch, $"{RecipesPath}/{seeded.Id}", """{"name":"Anonymous Patch"}"""),
        };

        foreach (var (method, path, json) in requests)
        {
            var response = await SendJsonAsync(
                client,
                method,
                path,
                json,
                csrfToken: null,
                cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "{0} {1}", method, path);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.AuthRequired);
        }
    }

    [Fact]
    public async Task Single_user_system_actor_manages_recipes_without_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync(RecipesPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            RecipesPath,
            """{"name":"System Recipe"}""",
            csrfToken: null,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        (await client.GetAsync($"{RecipesPath}/{id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{RecipesPath}/{id}",
            """{"status":"retired"}""",
            csrfToken: null,
            cancellationToken);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(patched, cancellationToken)).GetProperty("status").GetString()
            .Should().Be("retired");

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(AuditEventTypes.Created, AuditEventTypes.Updated);
        rows.Should().OnlyContain(row => row.ActorUserId == null);
    }

    [Fact]
    public async Task Reads_write_no_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        var seeded = await SeedRecipeAsync("Read Only", SeedTimestamp, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await session.Client.GetAsync(RecipesPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{RecipesPath}/{seeded.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{RecipesPath}?limit=1&offset=0", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_writes_leave_no_partial_recipe_or_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var rejected = await CreateRecipeAsync(session, """{"name":"   "}""", cancellationToken);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await CountRecipesAsync(cancellationToken)).Should().Be(0, "a failed create leaves no recipe row");
        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty("a failed create leaves no audit row");

        var created = await CreateRecipeAsync(session, """{"name":"Survivor"}""", cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        var updatedAt = createdRoot.GetProperty("updated_at").GetDateTimeOffset();

        var invalidPatch = await PatchRecipeAsync(session, id, """{"prep_minutes":-1}""", cancellationToken);
        invalidPatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(invalidPatch, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("prep_minutes", out _).Should().BeTrue();

        var stored = await ReadStoredRecipeAsync(id, cancellationToken);
        stored.Name.Should().Be("Survivor");
        stored.PrepMinutes.Should().BeNull();
        stored.UpdatedAt.Should().BeCloseTo(updatedAt, TimeSpan.FromMicroseconds(1), "a failed patch leaves the row untouched");
        (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle()
            .Which.EventType.Should().Be(AuditEventTypes.Created, "a failed patch writes no audit row");
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
        string? json,
        string? csrfToken,
        CancellationToken cancellationToken)
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

        return await client.SendAsync(request, cancellationToken);
    }

    private static Task<HttpResponseMessage> CreateRecipeAsync(
        SignedInClient session,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            RecipesPath,
            json,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> PatchRecipeAsync(
        SignedInClient session,
        long id,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{RecipesPath}/{id}",
            json,
            session.CsrfToken,
            cancellationToken);

    private async Task<Recipe> SeedRecipeAsync(
        string name,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            Status = RecipeStatus.Normal,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        };

        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(cancellationToken);
        return recipe;
    }

    private async Task SeedRecipeRangeAsync(int count, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        for (var index = 1; index <= count; index++)
        {
            var name = $"Bulk {index:000}";
            var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
            context.Recipes.Add(new Recipe
            {
                Name = normalized.Display,
                NormalizedName = normalized.Normalized,
                Status = RecipeStatus.Normal,
                CreatedAt = SeedTimestamp.AddMinutes(index),
                UpdatedAt = SeedTimestamp.AddMinutes(index),
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<Recipe> ReadStoredRecipeAsync(long id, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Recipes.AsNoTracking()
            .SingleAsync(recipe => recipe.Id == id, cancellationToken);
    }

    private async Task<int> CountRecipesAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Recipes.CountAsync(cancellationToken);
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents.AsNoTracking()
            .OrderBy(row => row.Id)
            .ToListAsync(cancellationToken);
    }

    private static JsonNode Snapshot(string? json) => JsonNode.Parse(json!)!;

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Recipe Endpoints Household",
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

    private async Task SeedPermissionAsync(
        long userId,
        string permissionKey,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.UserPermissions.Add(new UserPermission
        {
            UserId = userId,
            PermissionKey = permissionKey,
        });
        await context.SaveChangesAsync(cancellationToken);
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
