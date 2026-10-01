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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of M2-08 ingredients (docs/API.md §22): CRUD, search
/// and limit behavior, the shared canonical-name/alias uniqueness namespace,
/// atomic alias creation, department validation, retire/delete semantics
/// through the usage seam (including the merge-reference FK), the route
/// permission matrix, and audit coverage for every write. Requires a Docker
/// daemon; run with <c>dotnet test --filter "Category!=Integration"</c> to skip
/// locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class IngredientEndpointsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string IngredientsPath = "/api/v1/ingredients";

    private const string AdminPassword = "root horse battery staple";

    private const string MemberPassword = "correct horse battery staple";

    private const string TestUserAgent = "ingredient-endpoints-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Admin_lifecycle_writes_expected_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var produce = await SeedDepartmentAsync("Produce", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            IngredientsPath,
            $$"""{"canonical_name":"  Tomato  ","department_id":{{produce.Id}},"aliases":["  Tomatoes ","tomatoe"]}""",
            session.CsrfToken,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        createdRoot.GetProperty("canonical_name").GetString().Should().Be("Tomato");
        createdRoot.GetProperty("department_id").GetInt64().Should().Be(produce.Id);
        createdRoot.GetProperty("is_active").GetBoolean().Should().BeTrue();
        createdRoot.GetProperty("aliases").GetArrayLength().Should().Be(2);
        createdRoot.GetProperty("aliases")[0].GetProperty("alias").GetString().Should().Be("Tomatoes");
        createdRoot.GetProperty("aliases")[1].GetProperty("alias").GetString().Should().Be("tomatoe");
        var firstAliasId = createdRoot.GetProperty("aliases")[0].GetProperty("id").GetInt64();

        var renamed = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{IngredientsPath}/{id}",
            """{"canonical_name":" Roma Tomato "}""",
            session.CsrfToken,
            cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(renamed, cancellationToken)).GetProperty("canonical_name").GetString()
            .Should().Be("Roma Tomato");

        var cleared = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{IngredientsPath}/{id}",
            """{"department_id":null}""",
            session.CsrfToken,
            cancellationToken);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(cleared, cancellationToken)).GetProperty("department_id").ValueKind
            .Should().Be(JsonValueKind.Null);

        var alias = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{IngredientsPath}/{id}/aliases",
            """{"alias":"Roma"}""",
            session.CsrfToken,
            cancellationToken);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasId = (await ReadJsonAsync(alias, cancellationToken)).GetProperty("id").GetInt64();

        var searched = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=roma", cancellationToken),
            cancellationToken);
        searched.GetArrayLength().Should().Be(1);
        searched[0].GetProperty("id").GetInt64().Should().Be(id);
        searched[0].GetProperty("aliases").GetArrayLength().Should().Be(3);

        var removed = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{id}/aliases/{firstAliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RetireAsync(session, id, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var secondRetire = await RetireAsync(session, id, cancellationToken);
        secondRetire.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(secondRetire, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            "created",
            "updated",
            "updated",
            "updated",
            "updated",
            "retired",
            "deleted");
        rows.Should().OnlyContain(row => row.EntityType == AuditEntityTypes.Ingredient);
        rows.Should().OnlyContain(row => row.ActorUserId == admin.Id);

        var entityId = id.ToString(CultureInfo.InvariantCulture);
        var ingredientRows = rows.Where(row => row.EntityId == entityId).ToArray();
        ingredientRows[0].BeforeJson.Should().BeNull();
        var createdSnapshot = Snapshot(ingredientRows[0].AfterJson!);
        createdSnapshot["canonical_name"]!.GetValue<string>().Should().Be("Tomato");
        createdSnapshot["normalized_name"]!.GetValue<string>().Should().Be("tomato");
        createdSnapshot["department_id"]!.GetValue<long>().Should().Be(produce.Id);
        createdSnapshot["aliases"]!.AsArray()
            .Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal("Tomatoes", "tomatoe");

        Snapshot(ingredientRows[1].BeforeJson!)["canonical_name"]!.GetValue<string>().Should().Be("Tomato");
        Snapshot(ingredientRows[1].AfterJson!)["canonical_name"]!.GetValue<string>().Should().Be("Roma Tomato");

        Snapshot(ingredientRows[2].BeforeJson!)["department_id"]!.GetValue<long>().Should().Be(produce.Id);
        Snapshot(ingredientRows[2].AfterJson!)["department_id"].Should().BeNull();

        Snapshot(ingredientRows[3].BeforeJson!)["aliases"]!.AsArray().Should().HaveCount(2);
        Snapshot(ingredientRows[3].AfterJson!)["aliases"]!.AsArray()
            .Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal("Tomatoes", "tomatoe", "Roma");
        Snapshot(ingredientRows[4].BeforeJson!)["aliases"]!.AsArray().Should().HaveCount(3);
        Snapshot(ingredientRows[4].AfterJson!)["aliases"]!.AsArray()
            .Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal("tomatoe", "Roma");

        Snapshot(ingredientRows[5].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(ingredientRows[5].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        Snapshot(ingredientRows[6].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        ingredientRows[6].AfterJson.Should().BeNull();
        Snapshot(ingredientRows[6].BeforeJson!)["aliases"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Create_conflicts_cover_the_shared_canonical_and_alias_namespace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var tomato = await SeedIngredientAsync(
            "Tomato",
            aliases: ["Tomatoes"],
            cancellationToken: cancellationToken);
        var tomatoAliasId = await ReadAliasIdAsync(tomato.Id, "tomatoes", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var canonicalVariants = new[] { "TOMATO", "  tomatoes  ", "Tomáto" };
        foreach (var canonical in canonicalVariants)
        {
            var conflict = await SendJsonAsync(
                session.Client,
                HttpMethod.Post,
                IngredientsPath,
                JsonSerializer.Serialize(new { canonical_name = canonical }),
                session.CsrfToken,
                cancellationToken);
            conflict.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                "'{0}' collides with the existing tomato in the shared namespace",
                canonical);
            var error = await ReadErrorAsync(conflict, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.IngredientExists);
            error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(tomato.Id);
        }

        var aliasVsCanonical = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"Basil","aliases":["tomato"]}""",
            session.CsrfToken,
            cancellationToken);
        aliasVsCanonical.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var aliasVsCanonicalError = await ReadErrorAsync(aliasVsCanonical, cancellationToken);
        aliasVsCanonicalError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        aliasVsCanonicalError.GetProperty("details").GetProperty("source").GetString()
            .Should().Be("canonical_name");
        aliasVsCanonicalError.GetProperty("details").GetProperty("existing_id").GetInt64()
            .Should().Be(tomato.Id);

        var aliasVsAlias = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"Oregano","aliases":["TOMATOES"]}""",
            session.CsrfToken,
            cancellationToken);
        aliasVsAlias.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var aliasVsAliasError = await ReadErrorAsync(aliasVsAlias, cancellationToken);
        aliasVsAliasError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        aliasVsAliasError.GetProperty("details").GetProperty("source").GetString().Should().Be("alias");
        aliasVsAliasError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(tomato.Id);
        aliasVsAliasError.GetProperty("details").GetProperty("alias_id").GetInt64().Should().Be(tomatoAliasId);

        var aliasVsOwnCanonical = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"Basil","aliases":["BASIL"]}""",
            session.CsrfToken,
            cancellationToken);
        aliasVsOwnCanonical.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var aliasVsOwnCanonicalError = await ReadErrorAsync(aliasVsOwnCanonical, cancellationToken);
        aliasVsOwnCanonicalError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        aliasVsOwnCanonicalError.GetProperty("details").GetProperty("source").GetString()
            .Should().Be("canonical_name");

        var duplicateInRequest = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"Basil","aliases":["Sweet Basil","sweet   basil"]}""",
            session.CsrfToken,
            cancellationToken);
        duplicateInRequest.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(duplicateInRequest, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("aliases", out _).Should().BeTrue();

        await using (var context = fixture.CreateContext())
        {
            (await context.Ingredients.AsNoTracking().CountAsync(cancellationToken)).Should().Be(
                1,
                "every failed create is atomic and writes no partial rows");
            (await context.IngredientAliases.AsNoTracking().CountAsync(cancellationToken)).Should().Be(1);
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty("the seeded row and failed creates write no audit row");
    }

    [Fact]
    public async Task Patch_rename_conflicts_and_self_exclusion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var tomato = await SeedIngredientAsync("Tomato", aliases: ["Tomatoes"], cancellationToken: cancellationToken);
        var basil = await SeedIngredientAsync("Basil", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        foreach (var canonical in new[] { "TOMATO", " tomatoes ", "Tomáto" })
        {
            var conflict = await PatchAsync(session, basil.Id, JsonSerializer.Serialize(new { canonical_name = canonical }), cancellationToken);
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict, "'{0}' collides with tomato", canonical);
            var error = await ReadErrorAsync(conflict, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.IngredientExists);
            error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(tomato.Id);
        }

        var selfRename = await PatchAsync(session, basil.Id, """{"canonical_name":"  BASIL "}""", cancellationToken);
        selfRename.StatusCode.Should().Be(HttpStatusCode.OK, "a no-op rename excludes the ingredient itself");
        (await ReadJsonAsync(selfRename, cancellationToken)).GetProperty("canonical_name").GetString()
            .Should().Be("BASIL");

        var blank = await PatchAsync(session, basil.Id, """{"canonical_name":"   "}""", cancellationToken);
        blank.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(blank, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("canonical_name", out _).Should().BeTrue();

        var wrongType = await PatchAsync(session, basil.Id, """{"canonical_name":null}""", cancellationToken);
        wrongType.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var empty = await PatchAsync(session, basil.Id, """{}""", cancellationToken);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(empty, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("body", out _).Should().BeTrue();

        var missing = await PatchAsync(session, 999999, """{"canonical_name":"Ghost"}""", cancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(missing, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.EventType.Should().Be("updated");
    }

    [Fact]
    public async Task Aliases_are_added_removed_searchable_and_audited()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var tomato = await SeedIngredientAsync("Tomato", cancellationToken: cancellationToken);
        var basil = await SeedIngredientAsync("Basil", aliases: ["Basilico"], cancellationToken: cancellationToken);
        var basilicoAliasId = await ReadAliasIdAsync(basil.Id, "basilico", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var added = await AddAliasAsync(session, tomato.Id, """{"alias":" Tomatoes "}""", cancellationToken);
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var addedRoot = await ReadJsonAsync(added, cancellationToken);
        var tomatoAliasId = addedRoot.GetProperty("id").GetInt64();
        addedRoot.GetProperty("alias").GetString().Should().Be("Tomatoes");

        var duplicate = await AddAliasAsync(session, tomato.Id, """{"alias":" tomatoes "}""", cancellationToken);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var duplicateError = await ReadErrorAsync(duplicate, cancellationToken);
        duplicateError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        duplicateError.GetProperty("details").GetProperty("entity_type").GetString().Should().Be("ingredient");
        duplicateError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(tomato.Id);
        duplicateError.GetProperty("details").GetProperty("source").GetString().Should().Be("alias");
        duplicateError.GetProperty("details").GetProperty("alias_id").GetInt64().Should().Be(tomatoAliasId);

        var ownCanonical = await AddAliasAsync(session, tomato.Id, """{"alias":"TOMATO"}""", cancellationToken);
        ownCanonical.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var ownCanonicalError = await ReadErrorAsync(ownCanonical, cancellationToken);
        ownCanonicalError.GetProperty("details").GetProperty("source").GetString().Should().Be("canonical_name");
        ownCanonicalError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(tomato.Id);

        var foreignCanonical = await AddAliasAsync(session, tomato.Id, """{"alias":"basil"}""", cancellationToken);
        foreignCanonical.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var foreignCanonicalError = await ReadErrorAsync(foreignCanonical, cancellationToken);
        foreignCanonicalError.GetProperty("details").GetProperty("source").GetString()
            .Should().Be("canonical_name");
        foreignCanonicalError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(basil.Id);

        var foreignAlias = await AddAliasAsync(session, tomato.Id, """{"alias":"BASILICO"}""", cancellationToken);
        foreignAlias.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var foreignAliasError = await ReadErrorAsync(foreignAlias, cancellationToken);
        foreignAliasError.GetProperty("details").GetProperty("source").GetString().Should().Be("alias");
        foreignAliasError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(basil.Id);
        foreignAliasError.GetProperty("details").GetProperty("alias_id").GetInt64().Should().Be(basilicoAliasId);

        var missing = await AddAliasAsync(session, 999999, """{"alias":"ghost"}""", cancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var search = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=tomatoes", cancellationToken),
            cancellationToken);
        search.GetArrayLength().Should().Be(1);
        search[0].GetProperty("id").GetInt64().Should().Be(tomato.Id);
        search[0].GetProperty("aliases").EnumerateArray()
            .Select(node => node.GetProperty("alias").GetString())
            .Should().Contain("Tomatoes");

        var foreignRemoval = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{basil.Id}/aliases/{tomatoAliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        foreignRemoval.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an alias id that belongs to another ingredient is not found");

        var removal = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{tomato.Id}/aliases/{tomatoAliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        removal.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var repeatedRemoval = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{tomato.Id}/aliases/{tomatoAliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        repeatedRemoval.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().OnlyContain(row => row.EventType == "updated");
        rows.Should().OnlyContain(row => row.EntityType == AuditEntityTypes.Ingredient);

        var tomatoEntityId = tomato.Id.ToString(CultureInfo.InvariantCulture);
        var tomatoRows = rows.Where(row => row.EntityId == tomatoEntityId).ToArray();
        tomatoRows.Should().HaveCount(2, "the failed alias additions write no audit row");
        Snapshot(tomatoRows[0].BeforeJson!)["aliases"]!.AsArray().Should().BeEmpty();
        Snapshot(tomatoRows[0].AfterJson!)["aliases"]!.AsArray()
            .Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal("Tomatoes");
        Snapshot(tomatoRows[1].BeforeJson!)["aliases"]!.AsArray().Should().HaveCount(1);
        Snapshot(tomatoRows[1].AfterJson!)["aliases"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ranks_prefix_before_substring_and_matches_aliases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        // The substring match is seeded first (lowest id); the canonical-prefix and
        // alias-prefix matches are seeded later, so prefix ranking — not id order —
        // must produce the result.
        var brown = await SeedIngredientAsync("Brown Sugar", cancellationToken: cancellationToken);
        var sugar = await SeedIngredientAsync("Sugar", aliases: ["cane sugar"], cancellationToken: cancellationToken);
        var syrup = await SeedIngredientAsync("Maple Syrup", aliases: ["sugar free syrup"], cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var bySubstring = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=sugar", cancellationToken),
            cancellationToken);
        bySubstring.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(
                new[] { sugar.Id, syrup.Id, brown.Id },
                "canonical and alias prefixes rank before the lower-id substring match");

        var byUpperCase = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=SUGAR", cancellationToken),
            cancellationToken);
        byUpperCase.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(new[] { sugar.Id, syrup.Id, brown.Id });

        var byAliasPrefix = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=cane", cancellationToken),
            cancellationToken);
        byAliasPrefix.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(sugar.Id);

        var byFullAlias = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=cane%20sugar", cancellationToken),
            cancellationToken);
        byFullAlias.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(sugar.Id);

        var bySecondAlias = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=sugar%20free", cancellationToken),
            cancellationToken);
        bySecondAlias.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(syrup.Id);
    }

    [Fact]
    public async Task Get_honors_include_retired_and_clamps_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);

        await SeedIngredientRangeAsync(101, cancellationToken);
        await SeedIngredientAsync("Retired Bulk", isActive: false, cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var defaults = await ReadJsonAsync(
            await session.Client.GetAsync(IngredientsPath, cancellationToken),
            cancellationToken);
        defaults.GetArrayLength().Should().Be(20, "limit defaults to 20");
        defaults[0].GetProperty("canonical_name").GetString().Should().Be("Bulk 001");
        defaults[19].GetProperty("canonical_name").GetString().Should().Be("Bulk 020");

        var clamped = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?limit=1000", cancellationToken),
            cancellationToken);
        clamped.GetArrayLength().Should().Be(100, "the limit is clamped to 100");

        foreach (var query in new[] { "?limit=0", "?limit=-1", "?limit=banana" })
        {
            var invalid = await session.Client.GetAsync($"{IngredientsPath}{query}", cancellationToken);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorAsync(invalid, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty("limit", out _).Should().BeTrue();
        }

        var invalidRetired = await session.Client.GetAsync(
            $"{IngredientsPath}?include_retired=maybe",
            cancellationToken);
        invalidRetired.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(invalidRetired, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("include_retired", out _).Should().BeTrue();

        var controlSearch = await session.Client.GetAsync($"{IngredientsPath}?search=%01", cancellationToken);
        controlSearch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(controlSearch, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("search", out _).Should().BeTrue();

        (await ReadJsonAsync(
                await session.Client.GetAsync($"{IngredientsPath}?search=Retired%20Bulk", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(0, "retired ingredients are excluded by default");
        (await ReadJsonAsync(
                await session.Client.GetAsync(
                    $"{IngredientsPath}?search=Retired%20Bulk&include_retired=true",
                    cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Departments_are_validated_and_cleared_explicitly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var produce = await SeedDepartmentAsync("Produce", cancellationToken: cancellationToken);
        var frozen = await SeedDepartmentAsync("Frozen", isActive: false, cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var withoutDepartment = await CreateAsync(session, """{"canonical_name":"Salt"}""", cancellationToken);
        withoutDepartment.StatusCode.Should().Be(HttpStatusCode.Created);
        var salt = await ReadJsonAsync(withoutDepartment, cancellationToken);
        var saltId = salt.GetProperty("id").GetInt64();
        salt.GetProperty("department_id").ValueKind.Should().Be(JsonValueKind.Null);

        var invalidCreates = new (string Json, string Field)[]
        {
            ($$"""{"canonical_name":"Flour","department_id":{{frozen.Id}}}""", "department_id"),
            ("""{"canonical_name":"Flour","department_id":999999}""", "department_id"),
            ("""{"canonical_name":"Flour","department_id":"produce"}""", "department_id"),
        };
        foreach (var (json, field) in invalidCreates)
        {
            var response = await CreateAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' must fail", json);
            var error = await ReadErrorAsync(response, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
            error.GetProperty("details").GetProperty("field_errors").TryGetProperty(field, out _).Should().BeTrue();
        }

        var assigned = await PatchAsync(
            session,
            saltId,
            $$"""{"department_id":{{produce.Id}}}""",
            cancellationToken);
        assigned.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(assigned, cancellationToken)).GetProperty("department_id").GetInt64()
            .Should().Be(produce.Id);

        var inactive = await PatchAsync(
            session,
            saltId,
            $$"""{"department_id":{{frozen.Id}}}""",
            cancellationToken);
        inactive.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(inactive, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("department_id", out _).Should().BeTrue();

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == saltId, cancellationToken);
            stored.DepartmentId.Should().Be(produce.Id, "a failed department change leaves the link untouched");
        }

        var cleared = await PatchAsync(session, saltId, """{"department_id":null}""", cancellationToken);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(cleared, cancellationToken)).GetProperty("department_id").ValueKind
            .Should().Be(JsonValueKind.Null);

        var clearedAgain = await PatchAsync(session, saltId, """{"department_id":null}""", cancellationToken);
        clearedAgain.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Retire_is_idempotent_and_writes_a_single_audit_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var salt = await SeedIngredientAsync("Salt", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await RetireAsync(session, salt.Id, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await RetireAsync(session, salt.Id, cancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(second, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        (await RetireAsync(session, 999999, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle()
            .Which.EventType.Should().Be("retired");
    }

    [Fact]
    public async Task Delete_consults_the_reference_usage_seam()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var checker = new CountingReferenceUsageChecker { IngredientCount = 0 };
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken, checker);
        var unused = await SeedIngredientAsync(
            "Unused",
            aliases: ["unused alias"],
            cancellationToken: cancellationToken);
        var inUse = await SeedIngredientAsync("In Use", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{unused.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, "a zero usage count permits deletion");

        await using (var context = fixture.CreateContext())
        {
            (await context.Ingredients.AsNoTracking()
                    .AnyAsync(ingredient => ingredient.Id == unused.Id, cancellationToken))
                .Should().BeFalse();
            (await context.IngredientAliases.AsNoTracking()
                    .AnyAsync(alias => alias.IngredientId == unused.Id, cancellationToken))
                .Should().BeFalse("aliases cascade with the ingredient");
        }

        checker.IngredientCount = 3;
        var blocked = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{inUse.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await ReadErrorAsync(blocked, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.IngredientInUse);
        error.GetProperty("details").GetProperty("usage_count").GetInt32().Should().Be(3);
        error.GetProperty("details").TryGetProperty("reason", out _).Should().BeFalse();

        await using (var context = fixture.CreateContext())
        {
            (await context.Ingredients.AsNoTracking()
                    .AnyAsync(ingredient => ingredient.Id == inUse.Id, cancellationToken))
                .Should().BeTrue("a blocked delete must leave the ingredient in place");
        }

        var missing = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/999999",
            json: null,
            session.CsrfToken,
            cancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.EventType.Should().Be("deleted");
    }

    [Fact]
    public async Task Delete_is_blocked_by_a_merge_reference()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var source = await SeedIngredientAsync("Old Name", isActive: false, cancellationToken: cancellationToken);
        await SeedIngredientAsync(
            "New Name",
            mergedIntoIngredientId: source.Id,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var blocked = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{source.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await ReadErrorAsync(blocked, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.IngredientInUse);
        error.GetProperty("details").GetProperty("usage_count").GetInt32().Should().Be(0);
        error.GetProperty("details").GetProperty("reason").GetString().Should().Be("merge_reference");

        await using (var context = fixture.CreateContext())
        {
            (await context.Ingredients.AsNoTracking()
                    .AnyAsync(ingredient => ingredient.Id == source.Id, cancellationToken))
                .Should().BeTrue("the merge target must survive for traceability");
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reads_and_failed_writes_write_no_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var salt = await SeedIngredientAsync("Salt", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await session.Client.GetAsync(IngredientsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{IngredientsPath}?search=salt&include_retired=true", cancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{IngredientsPath}?limit=banana", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var missingName = await CreateAsync(session, """{"aliases":["ghost"]}""", cancellationToken);
        missingName.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var blankAlias = await AddAliasAsync(session, salt.Id, """{"alias":"   "}""", cancellationToken);
        blankAlias.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var emptyPatch = await PatchAsync(session, salt.Id, """{}""", cancellationToken);
        emptyPatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", IngredientsPath, null, HttpStatusCode.OK)]
    [InlineData("POST", IngredientsPath, """{"canonical_name":"denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("PATCH", "/api/v1/ingredients/1", """{"canonical_name":"Denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/v1/ingredients/1/aliases", """{"alias":"denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "/api/v1/ingredients/1/aliases/1", null, HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/v1/ingredients/1/retire", null, HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "/api/v1/ingredients/1", null, HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/v1/ingredients/merge", """{"source_id":1,"target_id":2}""", HttpStatusCode.Forbidden)]
    public async Task Member_without_grants_reads_but_cannot_write(
        string method,
        string path,
        string? json,
        HttpStatusCode expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        var memberResponse = await SendJsonAsync(
            session.Client,
            new HttpMethod(method),
            path,
            json,
            session.CsrfToken,
            cancellationToken);
        memberResponse.StatusCode.Should().Be(expected, "the member performs {0} {1}", method, path);
        if (expected == HttpStatusCode.Forbidden)
        {
            (await ReadErrorAsync(memberResponse, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        using var anonymous = factory.CreateDefaultClient();
        var anonymousResponse = await SendJsonAsync(
            anonymous,
            new HttpMethod(method),
            path,
            json,
            csrfToken: null,
            cancellationToken);
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(anonymousResponse, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Member_with_reference_data_edit_can_write_but_cannot_delete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var jane = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(jane.Id, PermissionKeys.ReferenceDataEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var seeded = await SeedIngredientAsync("Seed", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        (await session.Client.GetAsync(IngredientsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var created = await CreateAsync(session, """{"canonical_name":"Member Pickle"}""", cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdId = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        var renamed = await PatchAsync(
            session,
            createdId,
            """{"canonical_name":"Member Relish"}""",
            cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var alias = await AddAliasAsync(session, createdId, """{"alias":"memberalias"}""", cancellationToken);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasId = (await ReadJsonAsync(alias, cancellationToken)).GetProperty("id").GetInt64();

        (await session.Client.GetAsync($"{IngredientsPath}?search=memberalias", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var removed = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{createdId}/aliases/{aliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RetireAsync(session, createdId, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{seeded.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorAsync(deleted, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(row => row.ActorUserId == jane.Id);
    }

    [Fact]
    public async Task Single_user_system_actor_manages_ingredients_without_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        await SeedIngredientAsync("System Seed", cancellationToken: cancellationToken);

        using var client = factory.CreateClient();
        (await client.GetAsync(IngredientsPath, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"System Salt"}""",
            csrfToken: null,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdId = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{IngredientsPath}/{createdId}",
            """{"canonical_name":"System Sea Salt"}""",
            csrfToken: null,
            cancellationToken);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var alias = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{IngredientsPath}/{createdId}/aliases",
            """{"alias":"sysalias"}""",
            csrfToken: null,
            cancellationToken);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasId = (await ReadJsonAsync(alias, cancellationToken)).GetProperty("id").GetInt64();

        var removed = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{createdId}/aliases/{aliasId}",
            json: null,
            csrfToken: null,
            cancellationToken);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{IngredientsPath}/{createdId}/retire",
            json: null,
            csrfToken: null,
            cancellationToken);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{createdId}",
            json: null,
            csrfToken: null,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            "created",
            "updated",
            "updated",
            "updated",
            "retired",
            "deleted");
        rows.Should().OnlyContain(row => row.ActorUserId == null);
    }

    [Fact]
    public async Task Patch_rename_to_own_alias_is_allowed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var tomato = await SeedIngredientAsync("Tomato", aliases: ["Tomatoes"], cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var renamed = await PatchAsync(
            session,
            tomato.Id,
            """{"canonical_name":"tomatoes"}""",
            cancellationToken);
        renamed.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a rename excludes the ingredient's own alias rows");
        var root = await ReadJsonAsync(renamed, cancellationToken);
        root.GetProperty("canonical_name").GetString().Should().Be("tomatoes");
        root.GetProperty("aliases").GetArrayLength().Should().Be(1);

        var search = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=tomatoes", cancellationToken),
            cancellationToken);
        search.GetArrayLength().Should().Be(1);
        search[0].GetProperty("id").GetInt64().Should().Be(tomato.Id);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.EventType.Should().Be("updated");
        Snapshot(rows[0].BeforeJson!)["canonical_name"]!.GetValue<string>().Should().Be("Tomato");
        Snapshot(rows[0].AfterJson!)["canonical_name"]!.GetValue<string>().Should().Be("tomatoes");
    }

    [Fact]
    public async Task Retired_ingredients_still_own_their_namespace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var retired = await SeedIngredientAsync(
            "Retired Basil",
            isActive: false,
            aliases: ["Old Basil"],
            cancellationToken: cancellationToken);
        var retiredAliasId = await ReadAliasIdAsync(retired.Id, "old basil", cancellationToken);
        var parsley = await SeedIngredientAsync("Active Parsley", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var canonicalConflict = await CreateAsync(
            session,
            """{"canonical_name":"Retired Basil"}""",
            cancellationToken);
        canonicalConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var canonicalError = await ReadErrorAsync(canonicalConflict, cancellationToken);
        canonicalError.GetProperty("code").GetString().Should().Be(ErrorCodes.IngredientExists);
        canonicalError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(retired.Id);

        var aliasConflict = await CreateAsync(
            session,
            """{"canonical_name":"Fresh Basil","aliases":["OLD BASIL"]}""",
            cancellationToken);
        aliasConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var aliasError = await ReadErrorAsync(aliasConflict, cancellationToken);
        aliasError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        aliasError.GetProperty("details").GetProperty("source").GetString().Should().Be("alias");
        aliasError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(retired.Id);
        aliasError.GetProperty("details").GetProperty("alias_id").GetInt64().Should().Be(retiredAliasId);

        var addConflict = await AddAliasAsync(
            session,
            parsley.Id,
            """{"alias":"retired basil"}""",
            cancellationToken);
        addConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var addError = await ReadErrorAsync(addConflict, cancellationToken);
        addError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        addError.GetProperty("details").GetProperty("source").GetString().Should().Be("canonical_name");
        addError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(retired.Id);

        (await ReadJsonAsync(
                await session.Client.GetAsync($"{IngredientsPath}?search=Retired%20Basil", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(0, "retired rows stay out of the default list");
        (await ReadJsonAsync(
                await session.Client.GetAsync(
                    $"{IngredientsPath}?search=Retired%20Basil&include_retired=true",
                    cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(1);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Timestamps_are_written_and_bumped_on_every_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        // Whole-second current UTC: deterministic for assertions, while the
        // client-side CookieContainer still sees the session cookie as unexpired
        // (a date in the past would be dropped before the request is sent).
        var start = DateTimeOffset.UtcNow;
        start -= TimeSpan.FromTicks(start.Ticks % TimeSpan.TicksPerSecond);
        var clock = new TestTimeProvider(start);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken, timeProvider: clock);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await CreateAsync(session, """{"canonical_name":"Dated Salt"}""", cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        createdRoot.GetProperty("created_at").GetDateTimeOffset().Should().Be(start);
        createdRoot.GetProperty("updated_at").GetDateTimeOffset().Should().Be(start);

        clock.Advance(TimeSpan.FromMinutes(5));
        var patched = await PatchAsync(
            session,
            id,
            """{"canonical_name":"Dated Sea Salt"}""",
            cancellationToken);
        var patchedRoot = await ReadJsonAsync(patched, cancellationToken);
        patchedRoot.GetProperty("created_at").GetDateTimeOffset().Should().Be(start);
        patchedRoot.GetProperty("updated_at").GetDateTimeOffset().Should().Be(start.AddMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(5));
        var alias = await AddAliasAsync(session, id, """{"alias":"dated alias"}""", cancellationToken);
        var aliasId = (await ReadJsonAsync(alias, cancellationToken)).GetProperty("id").GetInt64();
        var afterAliasAdd = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=dated%20alias", cancellationToken),
            cancellationToken);
        afterAliasAdd[0].GetProperty("created_at").GetDateTimeOffset().Should().Be(start);
        afterAliasAdd[0].GetProperty("updated_at").GetDateTimeOffset().Should().Be(start.AddMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(5));
        var removed = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{id}/aliases/{aliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterAliasRemove = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=Dated%20Sea%20Salt", cancellationToken),
            cancellationToken);
        afterAliasRemove[0].GetProperty("created_at").GetDateTimeOffset().Should().Be(start);
        afterAliasRemove[0].GetProperty("updated_at").GetDateTimeOffset().Should().Be(start.AddMinutes(15));

        clock.Advance(TimeSpan.FromMinutes(5));
        var retired = await RetireAsync(session, id, cancellationToken);
        var retiredRoot = await ReadJsonAsync(retired, cancellationToken);
        retiredRoot.GetProperty("created_at").GetDateTimeOffset().Should().Be(start);
        retiredRoot.GetProperty("updated_at").GetDateTimeOffset().Should().Be(start.AddMinutes(20));

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == id, cancellationToken);
            stored.CreatedAt.Should().Be(start);
            stored.UpdatedAt.Should().Be(start.AddMinutes(20));
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().HaveCount(5);
    }

    [Fact]
    public async Task Explicit_transaction_remains_usable_after_a_unique_violation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        context.Ingredients.Add(new Ingredient
        {
            CanonicalName = "Savepoint Probe",
            NormalizedName = "savepoint probe",
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            context.Ingredients.Add(new Ingredient
            {
                CanonicalName = "Savepoint Probe",
                NormalizedName = "savepoint probe",
                IsActive = true,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
            });

            var save = async () => await context.SaveChangesAsync(cancellationToken);
            await save.Should().ThrowAsync<DbUpdateException>();
            context.ChangeTracker.Clear();

            // EF Core (Npgsql) wraps each SaveChanges inside an explicit transaction
            // in a savepoint and rolls back to it on failure, so the failed write
            // does not abort the surrounding transaction: the same context must
            // still be able to run the post-conflict re-query (no 25P02 "current
            // transaction is aborted").
            var count = await context.Ingredients.AsNoTracking()
                .CountAsync(ingredient => ingredient.NormalizedName == "savepoint probe", cancellationToken);
            count.Should().Be(1);

            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            context.ChangeTracker.Clear();
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM ingredients WHERE normalized_name = 'savepoint probe';",
                cancellationToken);
        }
    }

    [Fact]
    public async Task Merge_moves_free_aliases_copies_canonical_and_retires_source()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var start = DateTimeOffset.UtcNow;
        start -= TimeSpan.FromTicks(start.Ticks % TimeSpan.TicksPerSecond);
        var clock = new TestTimeProvider(start);
        var checker = new CountingReferenceUsageChecker { ReassignedCount = 3 };
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken, checker, clock);
        var source = await SeedIngredientAsync(
            "Old Tomato",
            aliases: ["Heirloom", "Ruby"],
            cancellationToken: cancellationToken);
        var target = await SeedIngredientAsync("Tomato", cancellationToken: cancellationToken);
        await SeedIngredientAsync("Ruby", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(5));

        var merged = await MergeAsync(session, source.Id, target.Id, cancellationToken);
        merged.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(merged, cancellationToken);
        root.GetProperty("source_id").GetInt64().Should().Be(source.Id);
        root.GetProperty("target_id").GetInt64().Should().Be(target.Id);
        root.GetProperty("skipped_aliases").EnumerateArray()
            .Select(node => node.GetString()).Should().Equal("Ruby");
        root.GetProperty("reassigned_reference_count").GetInt32().Should().Be(3);

        checker.LastReassignSourceId.Should().Be(source.Id, "the seam is called with the merge endpoints");
        checker.LastReassignTargetId.Should().Be(target.Id);

        await using (var context = fixture.CreateContext())
        {
            var storedSource = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == source.Id, cancellationToken);
            storedSource.IsActive.Should().BeFalse();
            storedSource.MergedIntoIngredientId.Should().Be(target.Id);
            storedSource.UpdatedAt.Should().Be(start.AddMinutes(5));

            var storedTarget = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == target.Id, cancellationToken);
            storedTarget.UpdatedAt.Should().Be(start.AddMinutes(5), "moving aliases bumps the target timestamp");

            var targetAliases = await context.IngredientAliases.AsNoTracking()
                .Where(alias => alias.IngredientId == target.Id)
                .OrderBy(alias => alias.Id)
                .Select(alias => alias.Alias)
                .ToListAsync(cancellationToken);
            targetAliases.Should().Equal("Old Tomato", "Heirloom");

            var sourceAliases = await context.IngredientAliases.AsNoTracking()
                .Where(alias => alias.IngredientId == source.Id)
                .OrderBy(alias => alias.Id)
                .Select(alias => alias.Alias)
                .ToListAsync(cancellationToken);
            sourceAliases.Should().Equal("Ruby");
        }

        var movedSearch = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=heirloom", cancellationToken),
            cancellationToken);
        movedSearch.GetArrayLength().Should().Be(1);
        movedSearch[0].GetProperty("id").GetInt64().Should().Be(target.Id);
    }

    [Fact]
    public async Task Merge_writes_one_audit_row_with_before_after_and_metadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var checker = new CountingReferenceUsageChecker { ReassignedCount = 3 };
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken, checker);
        var source = await SeedIngredientAsync(
            "Old Tomato",
            aliases: ["Heirloom", "Ruby"],
            cancellationToken: cancellationToken);
        var target = await SeedIngredientAsync("Tomato", cancellationToken: cancellationToken);
        await SeedIngredientAsync("Ruby", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await MergeAsync(session, source.Id, target.Id, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var row = (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle().Subject;
        row.EventType.Should().Be("merged");
        row.EntityType.Should().Be(AuditEntityTypes.Ingredient);
        row.EntityId.Should().Be(source.Id.ToString(CultureInfo.InvariantCulture));
        row.ActorUserId.Should().Be(admin.Id);

        var before = Snapshot(row.BeforeJson!);
        before["is_active"]!.GetValue<bool>().Should().BeTrue();
        before["merged_into_ingredient_id"].Should().BeNull();
        before["canonical_name"]!.GetValue<string>().Should().Be("Old Tomato");
        before["aliases"]!.AsArray().Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal("Heirloom", "Ruby");

        var after = Snapshot(row.AfterJson!);
        after["is_active"]!.GetValue<bool>().Should().BeFalse();
        after["merged_into_ingredient_id"]!.GetValue<long>().Should().Be(target.Id);
        after["aliases"]!.AsArray().Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal(new[] { "Ruby" }, "the skipped alias stays on the source");

        var metadata = Snapshot(row.MetadataJson);
        metadata["target_id"]!.GetValue<long>().Should().Be(target.Id);
        metadata["reassigned_reference_count"]!.GetValue<int>().Should().Be(3);
        metadata["skipped_aliases"]!.AsArray().Select(node => node!.GetValue<string>())
            .Should().Equal("Ruby");
    }

    [Fact]
    public async Task Merge_rolls_back_when_the_reference_seam_fails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var checker = new CountingReferenceUsageChecker
        {
            ReassignException = new InvalidOperationException("seam unavailable"),
        };
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken, checker);
        var source = await SeedIngredientAsync(
            "Rollback Source",
            aliases: ["Rollback Alias"],
            cancellationToken: cancellationToken);
        var target = await SeedIngredientAsync("Rollback Target", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var failed = await MergeAsync(session, source.Id, target.Id, cancellationToken);
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ReadErrorAsync(failed, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.InternalError);

        await using (var context = fixture.CreateContext())
        {
            var storedSource = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == source.Id, cancellationToken);
            storedSource.IsActive.Should().BeTrue("the failed merge must not retire the source");
            storedSource.MergedIntoIngredientId.Should().BeNull();

            var sourceAliases = await context.IngredientAliases.AsNoTracking()
                .Where(alias => alias.IngredientId == source.Id)
                .Select(alias => alias.Alias)
                .ToListAsync(cancellationToken);
            sourceAliases.Should().Equal("Rollback Alias");

            (await context.IngredientAliases.AsNoTracking()
                    .AnyAsync(alias => alias.IngredientId == target.Id, cancellationToken))
                .Should().BeFalse("no alias may be copied before the seam succeeds");
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty("the audit row rolls back with the merge");
    }

    [Fact]
    public async Task Merge_guards_cover_same_missing_inactive_and_already_merged_endpoints()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var activeSource = await SeedIngredientAsync("Guard Source", cancellationToken: cancellationToken);
        var activeTarget = await SeedIngredientAsync("Guard Target", cancellationToken: cancellationToken);
        var inactiveSource = await SeedIngredientAsync(
            "Inactive Guard Source",
            isActive: false,
            cancellationToken: cancellationToken);
        var inactiveTarget = await SeedIngredientAsync(
            "Inactive Guard Target",
            isActive: false,
            cancellationToken: cancellationToken);
        var mergedTarget = await SeedIngredientAsync("Merged Guard Target", cancellationToken: cancellationToken);
        var alreadyMergedSource = await SeedIngredientAsync(
            "Merged Guard Source",
            isActive: false,
            mergedIntoIngredientId: mergedTarget.Id,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var same = await MergeAsync(session, activeSource.Id, activeSource.Id, cancellationToken);
        same.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var sameError = await ReadErrorAsync(same, cancellationToken);
        sameError.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        sameError.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("source_id", out _).Should().BeTrue();

        var malformedBodies = new (string Json, string Field)[]
        {
            ("""{"target_id":1}""", "source_id"),
            ("""{"source_id":1}""", "target_id"),
            ("""{"source_id":"one","target_id":1}""", "source_id"),
            ("""{"source_id":1,"target_id":null}""", "target_id"),
        };
        foreach (var (json, field) in malformedBodies)
        {
            var malformed = await SendJsonAsync(
                session.Client,
                HttpMethod.Post,
                $"{IngredientsPath}/merge",
                json,
                session.CsrfToken,
                cancellationToken);
            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' must fail", json);
            (await ReadErrorAsync(malformed, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty(field, out _).Should().BeTrue();
        }

        var missing = await MergeAsync(session, 999998, 999999, cancellationToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(missing, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.NotFound);

        var inactiveSourceResponse = await MergeAsync(
            session,
            inactiveSource.Id,
            activeTarget.Id,
            cancellationToken);
        inactiveSourceResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(inactiveSourceResponse, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").GetProperty("source_id").EnumerateArray()
            .Select(node => node.GetString()).Should().Equal("inactive");

        var inactiveTargetResponse = await MergeAsync(
            session,
            activeSource.Id,
            inactiveTarget.Id,
            cancellationToken);
        inactiveTargetResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var inactiveTargetErrors = (await ReadErrorAsync(inactiveTargetResponse, cancellationToken))
            .GetProperty("details").GetProperty("field_errors");
        inactiveTargetErrors.GetProperty("target_id").EnumerateArray()
            .Select(node => node.GetString()).Should().Equal("inactive");
        inactiveTargetErrors.TryGetProperty("source_id", out _).Should().BeFalse();

        var alreadyMergedResponse = await MergeAsync(
            session,
            alreadyMergedSource.Id,
            mergedTarget.Id,
            cancellationToken);
        alreadyMergedResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(alreadyMergedResponse, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").GetProperty("source_id").EnumerateArray()
            .Select(node => node.GetString()).Should().Equal("already_merged");

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Merge_concurrent_requests_leave_one_winner_and_no_partial_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var source = await SeedIngredientAsync("Contended Source", cancellationToken: cancellationToken);
        var firstTarget = await SeedIngredientAsync("Contended Target A", cancellationToken: cancellationToken);
        var secondTarget = await SeedIngredientAsync("Contended Target B", cancellationToken: cancellationToken);
        using var firstSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        using var secondSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var responses = await Task.WhenAll(
            MergeAsync(firstSession, source.Id, firstTarget.Id, cancellationToken),
            MergeAsync(secondSession, source.Id, secondTarget.Id, cancellationToken));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK)
            .Should().Be(1, "the source row lock serializes the competing merges");
        var loser = responses.Single(response => response.StatusCode != HttpStatusCode.OK);
        new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound }.Should().Contain(
            loser.StatusCode,
            "the loser must be rejected, not merged twice");
        if (loser.StatusCode == HttpStatusCode.BadRequest)
        {
            (await ReadErrorAsync(loser, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").GetProperty("source_id").EnumerateArray()
                .Select(node => node.GetString()).Should().Contain("already_merged");
        }

        var winner = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
        var winnerTargetId = (await ReadJsonAsync(winner, cancellationToken)).GetProperty("target_id").GetInt64();
        new[] { firstTarget.Id, secondTarget.Id }.Should().Contain(winnerTargetId);
        var loserTargetId = winnerTargetId == firstTarget.Id ? secondTarget.Id : firstTarget.Id;

        await using (var context = fixture.CreateContext())
        {
            var storedSource = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == source.Id, cancellationToken);
            storedSource.IsActive.Should().BeFalse();
            storedSource.MergedIntoIngredientId.Should().Be(winnerTargetId);

            var winnerAliases = await context.IngredientAliases.AsNoTracking()
                .Where(alias => alias.IngredientId == winnerTargetId)
                .Select(alias => alias.Alias)
                .ToListAsync(cancellationToken);
            winnerAliases.Should().Equal("Contended Source");

            (await context.IngredientAliases.AsNoTracking()
                    .AnyAsync(alias => alias.IngredientId == loserTargetId, cancellationToken))
                .Should().BeFalse("the losing merge must not copy aliases");
            (await context.IngredientAliases.AsNoTracking()
                    .AnyAsync(alias => alias.IngredientId == source.Id, cancellationToken))
                .Should().BeFalse("the winning merge moved the canonical alias off the source");
        }

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.EventType.Should().Be("merged");
    }

    [Fact]
    public async Task Merge_moves_search_visibility_and_keeps_skipped_names_retired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var source = await SeedIngredientAsync(
            "Old Tomato",
            aliases: ["Heirloom", "Ruby"],
            cancellationToken: cancellationToken);
        var target = await SeedIngredientAsync("Tomato", cancellationToken: cancellationToken);
        await SeedIngredientAsync("Ruby", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await MergeAsync(session, source.Id, target.Id, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var movedSearch = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=heirloom", cancellationToken),
            cancellationToken);
        movedSearch.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(target.Id);

        var defaultCanonical = await ReadJsonAsync(
            await session.Client.GetAsync($"{IngredientsPath}?search=Old%20Tomato", cancellationToken),
            cancellationToken);
        defaultCanonical.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(
                new[] { target.Id },
                "the retired source is hidden while its copied alias still matches");

        var withRetired = await ReadJsonAsync(
            await session.Client.GetAsync(
                $"{IngredientsPath}?search=Old%20Tomato&include_retired=true",
                cancellationToken),
            cancellationToken);
        withRetired.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(source.Id, target.Id);
        withRetired[0].GetProperty("is_active").GetBoolean().Should().BeFalse();
        withRetired[0].GetProperty("aliases").EnumerateArray()
            .Select(node => node.GetProperty("alias").GetString()).Should().Equal("Ruby");
        withRetired[1].GetProperty("is_active").GetBoolean().Should().BeTrue();

        await using (var context = fixture.CreateContext())
        {
            var storedSource = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == source.Id, cancellationToken);
            storedSource.MergedIntoIngredientId.Should().Be(
                target.Id,
                "IngredientResponse does not expose the merge link, so it is read from storage");
        }

        var movedWithRetired = await ReadJsonAsync(
            await session.Client.GetAsync(
                $"{IngredientsPath}?search=heirloom&include_retired=true",
                cancellationToken),
            cancellationToken);
        movedWithRetired.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(
                new[] { target.Id },
                "the moved alias no longer matches the source");
    }

    [Fact]
    public async Task Merge_permission_matrix_member_with_reference_data_edit_can_merge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var jane = await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await SeedPermissionAsync(jane.Id, PermissionKeys.ReferenceDataEdit, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var source = await SeedIngredientAsync("Member Merge Source", cancellationToken: cancellationToken);
        var target = await SeedIngredientAsync("Member Merge Target", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        var merged = await MergeAsync(session, source.Id, target.Id, cancellationToken);
        merged.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = fixture.CreateContext())
        {
            var storedSource = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == source.Id, cancellationToken);
            storedSource.IsActive.Should().BeFalse();
            storedSource.MergedIntoIngredientId.Should().Be(target.Id);
        }

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.ActorUserId.Should().Be(jane.Id);
    }

    [Fact]
    public async Task Merge_permission_matrix_single_user_system_actor_can_merge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var source = await SeedIngredientAsync("System Merge Source", cancellationToken: cancellationToken);
        var target = await SeedIngredientAsync("System Merge Target", cancellationToken: cancellationToken);

        using var client = factory.CreateClient();
        var merged = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{IngredientsPath}/merge",
            JsonSerializer.Serialize(new { source_id = source.Id, target_id = target.Id }),
            csrfToken: null,
            cancellationToken);
        merged.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = fixture.CreateContext())
        {
            var storedSource = await context.Ingredients.AsNoTracking()
                .SingleAsync(ingredient => ingredient.Id == source.Id, cancellationToken);
            storedSource.IsActive.Should().BeFalse();
            storedSource.MergedIntoIngredientId.Should().Be(target.Id);
        }

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.ActorUserId.Should().BeNull(
            "single-user mode has no acting user");
    }

    [Fact]
    public async Task Merge_retired_source_still_owns_its_canonical_namespace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanIngredientsAsync(cancellationToken);
        var source = await SeedIngredientAsync("Legacy Spice", cancellationToken: cancellationToken);
        var target = await SeedIngredientAsync("Spice", cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await MergeAsync(session, source.Id, target.Id, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var conflict = await CreateAsync(session, """{"canonical_name":"LEGACY SPICE"}""", cancellationToken);
        conflict.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            "the retired source still owns its canonical name");
        var error = await ReadErrorAsync(conflict, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.IngredientExists);
        error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(source.Id);
    }

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed class CountingReferenceUsageChecker : IReferenceUsageChecker
    {
        public int UnitCount { get; set; }

        public int IngredientCount { get; set; }

        public int ProteinCount { get; set; }

        public int TagCount { get; set; }

        public int ReassignedCount { get; set; }

        public long? LastReassignSourceId { get; private set; }

        public long? LastReassignTargetId { get; private set; }

        public Exception? ReassignException { get; set; }

        public Task<int> CountUnitReferencesAsync(
            long unitId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(UnitCount);

        public Task<int> CountIngredientReferencesAsync(
            long ingredientId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(IngredientCount);

        public Task<int> CountProteinReferencesAsync(
            long proteinId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ProteinCount);

        public Task<int> CountTagReferencesAsync(
            long tagId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(TagCount);

        public Task<int> ReassignIngredientReferencesAsync(
            long sourceId,
            long targetId,
            CancellationToken cancellationToken = default)
        {
            LastReassignSourceId = sourceId;
            LastReassignTargetId = targetId;
            return ReassignException is null
                ? Task.FromResult(ReassignedCount)
                : Task.FromException<int>(ReassignException);
        }
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

    private static Task<HttpResponseMessage> CreateAsync(
        SignedInClient session,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session.Client, HttpMethod.Post, IngredientsPath, json, session.CsrfToken, cancellationToken);

    private static Task<HttpResponseMessage> PatchAsync(
        SignedInClient session,
        long id,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{IngredientsPath}/{id}",
            json,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> AddAliasAsync(
        SignedInClient session,
        long id,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{IngredientsPath}/{id}/aliases",
            json,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> RetireAsync(
        SignedInClient session,
        long id,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{IngredientsPath}/{id}/retire",
            json: null,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> MergeAsync(
        SignedInClient session,
        long sourceId,
        long targetId,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{IngredientsPath}/merge",
            JsonSerializer.Serialize(new { source_id = sourceId, target_id = targetId }),
            session.CsrfToken,
            cancellationToken);

    /// <summary>
    /// Boots the host (which runs the M2-04 startup seeder), then clears
    /// ingredients, their aliases, and departments so each test controls its
    /// own reference data. Pass a counting <see cref="IReferenceUsageChecker"/>
    /// fake to exercise the delete-usage seam; the default registration returns
    /// zero for every resource.
    /// </summary>
    private async Task<ApiFactory> StartFactoryWithCleanIngredientsAsync(
        CancellationToken cancellationToken,
        IReferenceUsageChecker? usageChecker = null,
        TimeProvider? timeProvider = null)
    {
        ApiFactory factory;
        if (usageChecker is null && timeProvider is null)
        {
            factory = fixture.CreateFactory();
        }
        else
        {
            var checker = usageChecker;
            var clock = timeProvider;
            factory = fixture.CreateFactory(configureServices: services =>
            {
                if (checker is not null)
                {
                    services.RemoveAll<IReferenceUsageChecker>();
                    services.AddSingleton(checker);
                }

                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
            });
        }

        using (factory.CreateClient())
        {
        }

        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM ingredient_aliases; DELETE FROM ingredients; DELETE FROM grocery_departments;",
            cancellationToken);
        return factory;
    }

    private async Task<GroceryDepartment> SeedDepartmentAsync(
        string name,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var department = new GroceryDepartment
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            SortOrder = 0,
            IsActive = isActive,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.GroceryDepartments.Add(department);
        await context.SaveChangesAsync(cancellationToken);
        return department;
    }

    private async Task<Ingredient> SeedIngredientAsync(
        string canonicalName,
        bool isActive = true,
        string[]? aliases = null,
        long? departmentId = null,
        long? mergedIntoIngredientId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(canonicalName)!;
        await using var context = fixture.CreateContext();
        var ingredient = new Ingredient
        {
            CanonicalName = normalizedName.Display,
            NormalizedName = normalizedName.Normalized,
            DepartmentId = departmentId,
            MergedIntoIngredientId = mergedIntoIngredientId,
            IsActive = isActive,
        };

        context.Ingredients.Add(ingredient);
        await context.SaveChangesAsync(cancellationToken);

        if (aliases is not null)
        {
            foreach (var alias in aliases)
            {
                var normalizedAlias = (ReferenceDataNormalizationResult.Success)
                    ReferenceDataNormalizer.Normalize(alias)!;
                context.IngredientAliases.Add(new IngredientAlias
                {
                    IngredientId = ingredient.Id,
                    Alias = normalizedAlias.Display,
                    NormalizedAlias = normalizedAlias.Normalized,
                });
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        return ingredient;
    }

    /// <summary>Seeds a batch of active ingredients in one SaveChanges for bulk scenarios.</summary>
    private async Task SeedIngredientRangeAsync(int count, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        for (var index = 1; index <= count; index++)
        {
            var display = $"Bulk {index:000}";
            var normalized = (ReferenceDataNormalizationResult.Success)
                ReferenceDataNormalizer.Normalize(display)!;
            context.Ingredients.Add(new Ingredient
            {
                CanonicalName = normalized.Display,
                NormalizedName = normalized.Normalized,
                IsActive = true,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<long> ReadAliasIdAsync(
        long ingredientId,
        string normalizedAlias,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.IngredientAliases.AsNoTracking()
            .Where(alias => alias.IngredientId == ingredientId && alias.NormalizedAlias == normalizedAlias)
            .Select(alias => alias.Id)
            .SingleAsync(cancellationToken);
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
            HouseholdName = "Ingredient Endpoints Household",
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
