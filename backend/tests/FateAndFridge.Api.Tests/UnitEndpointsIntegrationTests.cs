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
/// HTTP-level verification of units (docs/API.md §22): CRUD, search and
/// limit behavior, unit conflicts, dimension immutability, base-factor rules
/// and <c>factor_changed</c> audit snapshots, per-unit aliases, retire/delete
/// semantics through the usage seam, the route permission matrix, and audit
/// coverage for every write. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class UnitEndpointsIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string UnitsPath = "/api/v1/units";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "unit-endpoints-test";

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
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            UnitsPath,
            """{"symbol":"  tsp  ","display_name":"  teaspoon  ","dimension":"volume","base_factor":4.92892159375}""",
            session.CsrfToken,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        createdRoot.GetProperty("symbol").GetString().Should().Be("tsp");
        createdRoot.GetProperty("display_name").GetString().Should().Be("teaspoon");
        createdRoot.GetProperty("dimension").GetString().Should().Be("volume");
        createdRoot.GetProperty("base_factor").ValueKind.Should().Be(JsonValueKind.Number);
        createdRoot.GetProperty("base_factor").GetDecimal().Should().Be(4.92892159375m);
        createdRoot.GetProperty("is_custom").GetBoolean().Should().BeTrue();
        createdRoot.GetProperty("is_active").GetBoolean().Should().BeTrue();
        createdRoot.GetProperty("aliases").GetArrayLength().Should().Be(0);

        var countUnit = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            UnitsPath,
            """{"symbol":"pinch","display_name":"pinch","dimension":"count"}""",
            session.CsrfToken,
            cancellationToken);
        countUnit.StatusCode.Should().Be(HttpStatusCode.Created);
        var countRoot = await ReadJsonAsync(countUnit, cancellationToken);
        countRoot.GetProperty("base_factor").ValueKind.Should().Be(JsonValueKind.Null);
        countRoot.GetProperty("is_custom").GetBoolean().Should().BeTrue();

        var patched = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{UnitsPath}/{id}",
            """{"symbol":" tsp ","display_name":"Teaspoon"}""",
            session.CsrfToken,
            cancellationToken);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var patchedRoot = await ReadJsonAsync(patched, cancellationToken);
        patchedRoot.GetProperty("symbol").GetString().Should().Be("tsp");
        patchedRoot.GetProperty("display_name").GetString().Should().Be("Teaspoon");

        var factored = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{UnitsPath}/{id}",
            """{"base_factor":5.25}""",
            session.CsrfToken,
            cancellationToken);
        factored.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(factored, cancellationToken)).GetProperty("base_factor").GetDecimal()
            .Should().Be(5.25m);

        var alias = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{UnitsPath}/{id}/aliases",
            """{"alias":" teaspoons "}""",
            session.CsrfToken,
            cancellationToken);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasRoot = await ReadJsonAsync(alias, cancellationToken);
        var aliasId = aliasRoot.GetProperty("id").GetInt64();
        aliasRoot.GetProperty("alias").GetString().Should().Be("teaspoons");

        var list = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=teaspoon", cancellationToken),
            cancellationToken);
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("id").GetInt64().Should().Be(id);
        list[0].GetProperty("aliases").GetArrayLength().Should().Be(1);
        list[0].GetProperty("aliases")[0].GetProperty("alias").GetString().Should().Be("teaspoons");

        var removed = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{id}/aliases/{aliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RetireAsync(session, id, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var secondRetire = await RetireAsync(session, id, cancellationToken);
        secondRetire.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(secondRetire, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        (await ReadJsonAsync(
                await session.Client.GetAsync(UnitsPath, cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(1, "the active 'pinch' unit remains");
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{UnitsPath}?include_retired=true", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(2);

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            "created",
            "created",
            "updated",
            "factor_changed",
            "updated",
            "updated",
            "retired",
            "deleted");
        rows.Should().OnlyContain(row => row.EntityType == AuditEntityTypes.Unit);
        rows.Should().OnlyContain(row => row.ActorUserId == admin.Id);

        var entityId = id.ToString(CultureInfo.InvariantCulture);
        var tspRows = rows.Where(row => row.EntityId == entityId).ToArray();
        tspRows.Select(row => row.EventType).Should().Equal(
            "created",
            "updated",
            "factor_changed",
            "updated",
            "updated",
            "retired",
            "deleted");

        tspRows[0].BeforeJson.Should().BeNull();
        var createdSnapshot = Snapshot(tspRows[0].AfterJson!);
        createdSnapshot["symbol"]!.GetValue<string>().Should().Be("tsp");
        createdSnapshot["dimension"]!.GetValue<string>().Should().Be("volume");
        createdSnapshot["base_factor"]!.GetValue<decimal>().Should().Be(4.92892159375m);
        createdSnapshot["is_custom"]!.GetValue<bool>().Should().BeTrue();

        var factorRow = Snapshot(tspRows[2].BeforeJson!);
        factorRow["base_factor"]!.GetValue<decimal>().Should().Be(4.92892159375m);
        Snapshot(tspRows[2].AfterJson!)["base_factor"]!.GetValue<decimal>().Should().Be(5.25m);

        Snapshot(tspRows[3].BeforeJson!)["aliases"]!.AsArray().Should().BeEmpty();
        Snapshot(tspRows[3].AfterJson!)["aliases"]!.AsArray()
            .Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal("teaspoons");
        Snapshot(tspRows[4].BeforeJson!)["aliases"]!.AsArray().Should().HaveCount(1);
        Snapshot(tspRows[4].AfterJson!)["aliases"]!.AsArray().Should().BeEmpty();

        Snapshot(tspRows[5].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(tspRows[5].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        Snapshot(tspRows[6].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        tspRows[6].AfterJson.Should().BeNull();

        var pinchRow = rows.Single(row => row.EventType == "created" && row.EntityId != entityId);
        Snapshot(pinchRow.AfterJson!)["base_factor"].Should().BeNull();
    }

    [Fact]
    public async Task Create_conflicts_return_unit_exists_with_existing_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var first = await CreateUnitAsync(
            session,
            """{"symbol":"cup","display_name":"Cup","dimension":"volume","base_factor":236.5882365}""",
            cancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var existingId = (await ReadJsonAsync(first, cancellationToken)).GetProperty("id").GetInt64();

        var variants = new[]
        {
            """{"symbol":"cup","display_name":"Beaker","dimension":"volume","base_factor":1}""",
            """{"symbol":"CUP","display_name":"Mug","dimension":"volume","base_factor":1}""",
            """{"symbol":"beaker2","display_name":"  CÚP  ","dimension":"volume","base_factor":1}""",
        };

        foreach (var json in variants)
        {
            var conflict = await CreateUnitAsync(session, json, cancellationToken);
            conflict.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                "'{0}' conflicts with the existing 'cup' unit",
                json);
            var error = await ReadErrorAsync(conflict, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.UnitExists);
            error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(existingId);
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle()
            .Which.EventType.Should().Be("created");
    }

    [Fact]
    public async Task Create_conflicts_fold_stored_symbol_diacritics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        // Stored symbol carries diacritics; the incoming plain form must still
        // conflict because both sides fold through ReferenceDataNormalizer.
        var accented = await CreateUnitAsync(
            session,
            """{"symbol":"CÚP","display_name":"Cup One","dimension":"volume","base_factor":1}""",
            cancellationToken);
        accented.StatusCode.Should().Be(HttpStatusCode.Created);
        var accentedRoot = await ReadJsonAsync(accented, cancellationToken);
        var accentedId = accentedRoot.GetProperty("id").GetInt64();
        accentedRoot.GetProperty("symbol").GetString().Should().Be("CÚP");

        var plain = await CreateUnitAsync(
            session,
            """{"symbol":"cup","display_name":"Cup Two","dimension":"volume","base_factor":1}""",
            cancellationToken);
        plain.StatusCode.Should().Be(HttpStatusCode.Conflict, "the stored 'CÚP' folds to 'cup'");
        (await ReadErrorAsync(plain, cancellationToken)).GetProperty("details")
            .GetProperty("existing_id").GetInt64().Should().Be(accentedId);

        // Reverse direction: stored plain symbol, incoming diacritic variant.
        var pinch = await CreateUnitAsync(
            session,
            """{"symbol":"pinch","display_name":"Pinch One","dimension":"count"}""",
            cancellationToken);
        pinch.StatusCode.Should().Be(HttpStatusCode.Created);
        var pinchId = (await ReadJsonAsync(pinch, cancellationToken)).GetProperty("id").GetInt64();

        var accentedPinch = await CreateUnitAsync(
            session,
            """{"symbol":"PÍNCH","display_name":"Pinch Two","dimension":"count"}""",
            cancellationToken);
        accentedPinch.StatusCode.Should().Be(HttpStatusCode.Conflict, "'PÍNCH' folds to 'pinch'");
        (await ReadErrorAsync(accentedPinch, cancellationToken)).GetProperty("details")
            .GetProperty("existing_id").GetInt64().Should().Be(pinchId);

        (await ReadAuditRowsAsync(cancellationToken)).Should().HaveCount(
            2,
            "the folded-symbol conflicts write no audit row");
    }

    [Fact]
    public async Task Patch_rename_conflicts_and_self_exclusion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var cup = await CreateUnitAsync(
            session,
            """{"symbol":"cup","display_name":"Cup","dimension":"volume","base_factor":236.5882365}""",
            cancellationToken);
        cup.StatusCode.Should().Be(HttpStatusCode.Created);
        var cupId = (await ReadJsonAsync(cup, cancellationToken)).GetProperty("id").GetInt64();

        var beaker = await CreateUnitAsync(
            session,
            """{"symbol":"beaker2","display_name":"Beaker","dimension":"volume","base_factor":1}""",
            cancellationToken);
        beaker.StatusCode.Should().Be(HttpStatusCode.Created);
        var beakerId = (await ReadJsonAsync(beaker, cancellationToken)).GetProperty("id").GetInt64();

        // Rename to an existing normalized display name.
        var displayConflict = await PatchUnitAsync(
            session,
            beakerId,
            """{"display_name":" CÚP "}""",
            cancellationToken);
        displayConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var displayError = await ReadErrorAsync(displayConflict, cancellationToken);
        displayError.GetProperty("code").GetString().Should().Be(ErrorCodes.UnitExists);
        displayError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(cupId);

        // Rename to case and diacritic variants of another unit's symbol.
        foreach (var json in new[] { """{"symbol":"CUP"}""", """{"symbol":"cúp"}""" })
        {
            var symbolConflict = await PatchUnitAsync(session, beakerId, json, cancellationToken);
            symbolConflict.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                "'{0}' conflicts with the existing 'cup' unit",
                json);
            var error = await ReadErrorAsync(symbolConflict, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.UnitExists);
            error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(cupId);
        }

        // A no-op rename to the unit's own current name succeeds (self excluded).
        var selfRename = await PatchUnitAsync(session, cupId, """{"symbol":" cup "}""", cancellationToken);
        selfRename.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(selfRename, cancellationToken)).GetProperty("symbol").GetString()
            .Should().Be("cup");

        var renamed = await PatchUnitAsync(
            session,
            beakerId,
            """{"symbol":"beaker"}""",
            cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(renamed, cancellationToken)).GetProperty("symbol").GetString()
            .Should().Be("beaker");

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            "created",
            "created",
            "updated",
            "updated");
        rows.Count(row => row.EventType == AuditEventTypes.Updated).Should().Be(
            2,
            "the failed renames write no audit row");
    }


    [Fact]
    public async Task Create_validates_dimension_and_base_factor_rules()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var invalidCases = new (string Json, string Field)[]
        {
            ("""{"display_name":"x","dimension":"mass","base_factor":1}""", "symbol"),
            ("""{"symbol":"x","dimension":"mass","base_factor":1}""", "display_name"),
            ("""{"symbol":"x","display_name":"x","base_factor":1}""", "dimension"),
            ("""{"symbol":"x","display_name":"x","dimension":"weight"}""", "dimension"),
            ("""{"symbol":"x","display_name":"x","dimension":5}""", "dimension"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass"}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass","base_factor":null}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"count","base_factor":1}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"nonconvertible","base_factor":1}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass","base_factor":0}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass","base_factor":-5}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass","base_factor":0.0000001}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass","base_factor":1000001}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass","base_factor":"4.9"}""", "base_factor"),
            ("""{"symbol":"x","display_name":"x","dimension":"mass","base_factor":1e40}""", "base_factor"),
            ("""{"symbol":5,"display_name":"x","dimension":"count"}""", "symbol"),
            ("""{"symbol":"x","display_name":5,"dimension":"count"}""", "display_name"),
        };

        foreach (var (json, field) in invalidCases)
        {
            var response = await CreateUnitAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' must fail", json);
            var error = await ReadErrorAsync(response, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
            error.GetProperty("details").GetProperty("field_errors").TryGetProperty(field, out _)
                .Should().BeTrue("'{0}' must report a {1} error", json, field);
        }

        var successes = new (string Json, decimal? Factor)[]
        {
            ("""{"symbol":"micro","display_name":"micro unit","dimension":"mass","base_factor":0.000001}""", 0.000001m),
            ("""{"symbol":"mega","display_name":"mega unit","dimension":"mass","base_factor":1000000}""", 1000000m),
            ("""{"symbol":"dozen","display_name":"dozen","dimension":"count"}""", null),
            ("""{"symbol":"tt","display_name":"to taste","dimension":"nonconvertible"}""", null),
        };

        foreach (var (json, factor) in successes)
        {
            var response = await CreateUnitAsync(session, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.Created, "'{0}' must succeed", json);
            var root = await ReadJsonAsync(response, cancellationToken);
            if (factor is null)
            {
                root.GetProperty("base_factor").ValueKind.Should().Be(JsonValueKind.Null);
            }
            else
            {
                root.GetProperty("base_factor").ValueKind.Should().Be(JsonValueKind.Number);
                root.GetProperty("base_factor").GetDecimal().Should().Be(factor.Value);
            }
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().HaveCount(
            successes.Length,
            "failed validation writes no audit row");
    }

    [Fact]
    public async Task Patch_factor_changed_audits_before_and_after_and_enforces_bounds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        var ounce = await SeedUnitAsync(
            "oz",
            "ounce",
            UnitDimension.Mass,
            28.349523125m,
            cancellationToken: cancellationToken);
        var dozen = await SeedUnitAsync(
            "dozen",
            "dozen",
            UnitDimension.Count,
            baseFactor: null,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var read = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=ounce", cancellationToken),
            cancellationToken);
        read.GetArrayLength().Should().Be(1);
        read[0].GetProperty("base_factor").ValueKind.Should().Be(JsonValueKind.Number);
        read[0].GetProperty("base_factor").GetDecimal().Should().Be(
            28.349523125m,
            "the stored numeric(28,14) value round-trips exactly");

        var changed = await PatchUnitAsync(session, ounce.Id, """{"base_factor":30.5}""", cancellationToken);
        changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(changed, cancellationToken)).GetProperty("base_factor").GetDecimal()
            .Should().Be(30.5m);

        var sameValue = await PatchUnitAsync(session, ounce.Id, """{"base_factor":30.5}""", cancellationToken);
        sameValue.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var json in new[]
        {
            """{"base_factor":0.000001}""",
            """{"base_factor":1000000}""",
        })
        {
            var response = await PatchUnitAsync(session, ounce.Id, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "'{0}' is inside the inclusive bounds", json);
        }

        foreach (var json in new[]
        {
            """{"base_factor":0.0000001}""",
            """{"base_factor":1000001}""",
            """{"base_factor":0}""",
            """{"base_factor":-2}""",
            """{"base_factor":null}""",
            """{"base_factor":"1.5"}""",
            """{"base_factor":1e40}""",
            """{}""",
        })
        {
            var response = await PatchUnitAsync(session, ounce.Id, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' must fail", json);
            var error = await ReadErrorAsync(response, cancellationToken);
            var expectedField = json == "{}" ? "body" : "base_factor";
            error.GetProperty("details").GetProperty("field_errors").TryGetProperty(expectedField, out _)
                .Should().BeTrue("'{0}' must report a {1} error", json, expectedField);
        }

        var forbidden = await PatchUnitAsync(session, dozen.Id, """{"base_factor":5}""", cancellationToken);
        forbidden.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(forbidden, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("base_factor", out _).Should().BeTrue();

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            "factor_changed",
            "updated",
            "factor_changed",
            "factor_changed");
        Snapshot(rows[0].BeforeJson!)["base_factor"]!.GetValue<decimal>().Should().Be(28.349523125m);
        Snapshot(rows[0].AfterJson!)["base_factor"]!.GetValue<decimal>().Should().Be(30.5m);
        Snapshot(rows[1].BeforeJson!)["base_factor"]!.GetValue<decimal>().Should().Be(30.5m);
        Snapshot(rows[1].AfterJson!)["base_factor"]!.GetValue<decimal>().Should().Be(30.5m);
        Snapshot(rows[2].AfterJson!)["base_factor"]!.GetValue<decimal>().Should().Be(0.000001m);
        Snapshot(rows[3].AfterJson!)["base_factor"]!.GetValue<decimal>().Should().Be(1000000m);
    }

    [Fact]
    public async Task Patch_dimension_is_immutable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        var cup = await SeedUnitAsync(
            "cup",
            "cup",
            UnitDimension.Volume,
            236.5882365m,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        foreach (var json in new[]
        {
            """{"dimension":"mass"}""",
            """{"dimension":"volume"}""",
            """{"symbol":"tbsp","dimension":"mass"}""",
            """{"dimension":null}""",
        })
        {
            var response = await PatchUnitAsync(session, cup.Id, json, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' must fail", json);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.InvalidUnitDimensionChange);
        }

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Units.AsNoTracking()
                .SingleAsync(unit => unit.Id == cup.Id, cancellationToken);
            stored.Dimension.Should().Be(UnitDimension.Volume);
            stored.Symbol.Should().Be("cup");
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Aliases_are_per_unit_added_removed_searchable_and_audited()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        var teaspoon = await SeedUnitAsync(
            "tsp",
            "teaspoon",
            UnitDimension.Volume,
            4.92892159375m,
            cancellationToken: cancellationToken);
        var tablespoon = await SeedUnitAsync(
            "tbsp",
            "tablespoon",
            UnitDimension.Volume,
            14.78676478125m,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var addTeaspoon = await AddAliasAsync(session, teaspoon.Id, """{"alias":"t"}""", cancellationToken);
        addTeaspoon.StatusCode.Should().Be(HttpStatusCode.Created);
        var teaspoonAlias = await ReadJsonAsync(addTeaspoon, cancellationToken);
        var teaspoonAliasId = teaspoonAlias.GetProperty("id").GetInt64();
        teaspoonAlias.GetProperty("alias").GetString().Should().Be("t");

        var addTablespoon = await AddAliasAsync(session, tablespoon.Id, """{"alias":"T"}""", cancellationToken);
        addTablespoon.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "aliases are unique per unit, so the seeded 't' and 'T' coexist");
        (await ReadJsonAsync(addTablespoon, cancellationToken)).GetProperty("alias").GetString()
            .Should().Be("T");

        var duplicate = await AddAliasAsync(session, teaspoon.Id, """{"alias":" t "}""", cancellationToken);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var duplicateError = await ReadErrorAsync(duplicate, cancellationToken);
        duplicateError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        duplicateError.GetProperty("details").GetProperty("entity_type").GetString().Should().Be("unit");
        duplicateError.GetProperty("details").GetProperty("existing_id").GetInt64()
            .Should().Be(teaspoon.Id);
        duplicateError.GetProperty("details").GetProperty("source").GetString().Should().Be("alias");
        duplicateError.GetProperty("details").GetProperty("alias_id").GetInt64()
            .Should().Be(teaspoonAliasId);

        var ownSymbol = await AddAliasAsync(session, teaspoon.Id, """{"alias":"TSP"}""", cancellationToken);
        ownSymbol.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var ownSymbolError = await ReadErrorAsync(ownSymbol, cancellationToken);
        ownSymbolError.GetProperty("code").GetString().Should().Be(ErrorCodes.AliasExists);
        ownSymbolError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(teaspoon.Id);
        ownSymbolError.GetProperty("details").GetProperty("source").GetString().Should().Be("symbol");

        var ownDisplay = await AddAliasAsync(
            session,
            teaspoon.Id,
            """{"alias":"Teaspoon"}""",
            cancellationToken);
        ownDisplay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(ownDisplay, cancellationToken)).GetProperty("details")
            .GetProperty("source").GetString().Should().Be("display_name");

        var searchAlias = await AddAliasAsync(
            session,
            tablespoon.Id,
            """{"alias":"tablesp"}""",
            cancellationToken);
        searchAlias.StatusCode.Should().Be(HttpStatusCode.Created);

        var searched = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=tablesp", cancellationToken),
            cancellationToken);
        searched.GetArrayLength().Should().Be(1);
        searched[0].GetProperty("id").GetInt64().Should().Be(tablespoon.Id);
        searched[0].GetProperty("aliases").EnumerateArray()
            .Select(alias => alias.GetProperty("alias").GetString())
            .Should().Contain("tablesp");

        var foreignRemoval = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{tablespoon.Id}/aliases/{teaspoonAliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        foreignRemoval.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an alias id that belongs to another unit is not found for this unit");

        var unknownRemoval = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{teaspoon.Id}/aliases/999999",
            json: null,
            session.CsrfToken,
            cancellationToken);
        unknownRemoval.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var removal = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{teaspoon.Id}/aliases/{teaspoonAliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        removal.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var repeatedRemoval = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{teaspoon.Id}/aliases/{teaspoonAliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        repeatedRemoval.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var afterRemoval = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=teaspoon", cancellationToken),
            cancellationToken);
        afterRemoval[0].GetProperty("aliases").GetArrayLength().Should().Be(0);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().OnlyContain(row => row.EventType == "updated");
        rows.Should().OnlyContain(row => row.EntityType == AuditEntityTypes.Unit);

        var teaspoonEntityId = teaspoon.Id.ToString(CultureInfo.InvariantCulture);
        var teaspoonRows = rows.Where(row => row.EntityId == teaspoonEntityId).ToArray();
        teaspoonRows.Should().HaveCount(2, "the failed alias additions write no audit row");
        Snapshot(teaspoonRows[0].BeforeJson!)["aliases"]!.AsArray().Should().BeEmpty();
        Snapshot(teaspoonRows[0].AfterJson!)["aliases"]!.AsArray()
            .Select(node => node!["alias"]!.GetValue<string>())
            .Should().Equal("t");
        Snapshot(teaspoonRows[1].BeforeJson!)["aliases"]!.AsArray().Should().HaveCount(1);
        Snapshot(teaspoonRows[1].AfterJson!)["aliases"]!.AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Get_ranks_prefix_before_substring_and_matches_aliases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);

        var scoop = await SeedUnitAsync(
            "scoop",
            "scoop",
            UnitDimension.Volume,
            15,
            aliases: ["measuring cup"],
            cancellationToken: cancellationToken);
        var cup = await SeedUnitAsync(
            "cup",
            "cup",
            UnitDimension.Volume,
            236.5882365m,
            aliases: ["cups"],
            cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var bySubstring = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=cup", cancellationToken),
            cancellationToken);
        bySubstring.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(new[] { cup.Id, scoop.Id }, "the symbol prefix ranks first");

        var byAliasPrefix = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=meas", cancellationToken),
            cancellationToken);
        byAliasPrefix.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(scoop.Id);

        var byUpperSymbol = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=SCOOP", cancellationToken),
            cancellationToken);
        byUpperSymbol.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(scoop.Id);

        var byFullAlias = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?search=measuring%20cup", cancellationToken),
            cancellationToken);
        byFullAlias.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(scoop.Id);
    }

    [Fact]
    public async Task Get_honors_include_retired_and_clamps_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);

        await SeedUnitRangeAsync(101, cancellationToken);
        await SeedUnitAsync(
            "zzz",
            "Retired Bulk",
            UnitDimension.Count,
            baseFactor: null,
            isActive: false,
            cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var defaults = await ReadJsonAsync(
            await session.Client.GetAsync(UnitsPath, cancellationToken),
            cancellationToken);
        defaults.GetArrayLength().Should().Be(20, "limit defaults to 20");
        defaults[0].GetProperty("display_name").GetString().Should().Be("Bulk 001");
        defaults[19].GetProperty("display_name").GetString().Should().Be("Bulk 020");

        var clamped = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?limit=1000", cancellationToken),
            cancellationToken);
        clamped.GetArrayLength().Should().Be(100, "the limit is clamped to 100");

        var explicitHundred = await ReadJsonAsync(
            await session.Client.GetAsync($"{UnitsPath}?limit=100", cancellationToken),
            cancellationToken);
        explicitHundred.GetArrayLength().Should().Be(100);

        foreach (var query in new[] { "?limit=0", "?limit=-1", "?limit=banana" })
        {
            var invalid = await session.Client.GetAsync($"{UnitsPath}{query}", cancellationToken);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorAsync(invalid, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty("limit", out _).Should().BeTrue();
        }

        var invalidRetired = await session.Client.GetAsync(
            $"{UnitsPath}?include_retired=maybe",
            cancellationToken);
        invalidRetired.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(invalidRetired, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("include_retired", out _).Should().BeTrue();

        var controlSearch = await session.Client.GetAsync($"{UnitsPath}?search=%01", cancellationToken);
        controlSearch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(controlSearch, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("search", out _).Should().BeTrue();

        (await ReadJsonAsync(
                await session.Client.GetAsync($"{UnitsPath}?search=Retired%20Bulk", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(0, "retired units are excluded by default");
        (await ReadJsonAsync(
                await session.Client.GetAsync(
                    $"{UnitsPath}?search=Retired%20Bulk&include_retired=true",
                    cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Retire_is_idempotent_and_writes_a_single_audit_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        var unit = await SeedUnitAsync(
            "each",
            "each",
            UnitDimension.Count,
            baseFactor: null,
            cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await RetireAsync(session, unit.Id, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await RetireAsync(session, unit.Id, cancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(second, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

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
        var checker = new CountingReferenceUsageChecker { UnitCount = 0 };
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken, checker);
        var unused = await SeedUnitAsync(
            "unused",
            "unused unit",
            UnitDimension.Count,
            baseFactor: null,
            cancellationToken: cancellationToken);
        var inUse = await SeedUnitAsync(
            "inuse",
            "in-use unit",
            UnitDimension.Count,
            baseFactor: null,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{unused.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent, "a zero usage count permits deletion");

        checker.UnitCount = 3;
        var blocked = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{inUse.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await ReadErrorAsync(blocked, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.UnitInUse);
        error.GetProperty("details").GetProperty("usage_count").GetInt32().Should().Be(3);

        await using (var context = fixture.CreateContext())
        {
            (await context.Units.AsNoTracking()
                    .AnyAsync(unit => unit.Id == inUse.Id, cancellationToken))
                .Should().BeTrue("a blocked delete must leave the unit in place");
        }

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.EventType.Should().Be("deleted");
    }

    [Fact]
    public async Task Reads_and_failed_writes_write_no_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        var cup = await SeedUnitAsync(
            "cup",
            "cup",
            UnitDimension.Volume,
            236.5882365m,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await session.Client.GetAsync(UnitsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{UnitsPath}?search=cup&include_retired=true", cancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{UnitsPath}?limit=banana", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var invalidCreate = await CreateUnitAsync(
            session,
            """{"symbol":"bad","display_name":"bad","dimension":"weight"}""",
            cancellationToken);
        invalidCreate.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var missingAlias = await AddAliasAsync(session, cup.Id, """{"alias":"  "}""", cancellationToken);
        missingAlias.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("GET", UnitsPath, null, HttpStatusCode.OK)]
    [InlineData("POST", UnitsPath, """{"symbol":"denied","display_name":"denied","dimension":"count"}""", HttpStatusCode.Forbidden)]
    [InlineData("PATCH", "/api/v1/units/1", """{"display_name":"Denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/v1/units/1/aliases", """{"alias":"denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "/api/v1/units/1/aliases/1", null, HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/v1/units/1/retire", null, HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "/api/v1/units/1", null, HttpStatusCode.Forbidden)]
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
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
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
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        var seeded = await SeedUnitAsync(
            "seedunit",
            "seed unit",
            UnitDimension.Count,
            baseFactor: null,
            cancellationToken: cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        (await session.Client.GetAsync(UnitsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var created = await CreateUnitAsync(
            session,
            """{"symbol":"memberunit","display_name":"member unit","dimension":"count"}""",
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdId = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        var renamed = await PatchUnitAsync(
            session,
            createdId,
            """{"display_name":"Member Renamed"}""",
            cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var alias = await AddAliasAsync(
            session,
            createdId,
            """{"alias":"memberalias"}""",
            cancellationToken);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasId = (await ReadJsonAsync(alias, cancellationToken)).GetProperty("id").GetInt64();

        (await session.Client.GetAsync($"{UnitsPath}?search=memberalias", cancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var removed = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{createdId}/aliases/{aliasId}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RetireAsync(session, createdId, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{UnitsPath}/{seeded.Id}",
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
    public async Task Single_user_system_actor_manages_units_without_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanUnitsAsync(cancellationToken);
        await SeedUnitAsync(
            "sysseed",
            "system seed",
            UnitDimension.Count,
            baseFactor: null,
            cancellationToken: cancellationToken);

        using var client = factory.CreateClient();
        (await client.GetAsync(UnitsPath, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            UnitsPath,
            """{"symbol":"sysunit","display_name":"system unit","dimension":"count"}""",
            csrfToken: null,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdId = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{UnitsPath}/{createdId}",
            """{"display_name":"System Renamed"}""",
            csrfToken: null,
            cancellationToken);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var alias = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{UnitsPath}/{createdId}/aliases",
            """{"alias":"sysalias"}""",
            csrfToken: null,
            cancellationToken);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasId = (await ReadJsonAsync(alias, cancellationToken)).GetProperty("id").GetInt64();

        var removed = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{UnitsPath}/{createdId}/aliases/{aliasId}",
            json: null,
            csrfToken: null,
            cancellationToken);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{UnitsPath}/{createdId}/retire",
            json: null,
            csrfToken: null,
            cancellationToken);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{UnitsPath}/{createdId}",
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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ReassignedCount);
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

    private static Task<HttpResponseMessage> CreateUnitAsync(
        SignedInClient session,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session.Client, HttpMethod.Post, UnitsPath, json, session.CsrfToken, cancellationToken);

    private static Task<HttpResponseMessage> PatchUnitAsync(
        SignedInClient session,
        long id,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{UnitsPath}/{id}",
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
            $"{UnitsPath}/{id}/aliases",
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
            $"{UnitsPath}/{id}/retire",
            json: null,
            session.CsrfToken,
            cancellationToken);

    /// <summary>
    /// Boots the host (which runs the startup seeder), then clears units
    /// and unit aliases so each test controls its own reference data. Pass a
    /// counting <see cref="IReferenceUsageChecker"/> fake to exercise the
    /// delete-usage seam; the default registration returns zero for every
    /// resource.
    /// </summary>
    private async Task<ApiFactory> StartFactoryWithCleanUnitsAsync(
        CancellationToken cancellationToken,
        IReferenceUsageChecker? usageChecker = null)
    {
        ApiFactory factory;
        if (usageChecker is null)
        {
            factory = fixture.CreateFactory();
        }
        else
        {
            var checker = usageChecker;
            factory = fixture.CreateFactory(configureServices: services =>
            {
                services.RemoveAll<IReferenceUsageChecker>();
                services.AddSingleton(checker);
            });
        }

        using (factory.CreateClient())
        {
        }

        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM unit_aliases; DELETE FROM units;",
            cancellationToken);
        return factory;
    }

    private async Task<Unit> SeedUnitAsync(
        string symbol,
        string displayName,
        UnitDimension dimension,
        decimal? baseFactor,
        bool isActive = true,
        string[]? aliases = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(symbol)!;
        var normalizedDisplay = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(displayName)!;
        await using var context = fixture.CreateContext();
        var unit = new Unit
        {
            Symbol = normalizedSymbol.Display,
            DisplayName = normalizedDisplay.Display,
            NormalizedName = normalizedDisplay.Normalized,
            Dimension = dimension,
            BaseFactor = baseFactor,
            IsCustom = false,
            IsActive = isActive,
        };

        context.Units.Add(unit);
        await context.SaveChangesAsync(cancellationToken);

        if (aliases is not null)
        {
            foreach (var alias in aliases)
            {
                var normalizedAlias = (ReferenceDataNormalizationResult.Success)
                    ReferenceDataNormalizer.Normalize(alias)!;
                context.UnitAliases.Add(new UnitAlias
                {
                    UnitId = unit.Id,
                    Alias = normalizedAlias.Display,
                    NormalizedAlias = normalizedAlias.Normalized,
                });
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        return unit;
    }

    /// <summary>Seeds a batch of active count units in one SaveChanges for bulk scenarios.</summary>
    private async Task SeedUnitRangeAsync(int count, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        for (var index = 1; index <= count; index++)
        {
            var display = $"Bulk {index:000}";
            var normalizedDisplay = (ReferenceDataNormalizationResult.Success)
                ReferenceDataNormalizer.Normalize(display)!;
            context.Units.Add(new Unit
            {
                Symbol = $"s{index:000}",
                DisplayName = normalizedDisplay.Display,
                NormalizedName = normalizedDisplay.Normalized,
                Dimension = UnitDimension.Count,
                BaseFactor = null,
                IsCustom = false,
                IsActive = true,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();

        // Mirrors login/logout into audit_events; these suites assert the
        // reference-data/recipe rows they themselves write, so the auth mirror is
        // excluded here.
        return await context.AuditEvents.AsNoTracking()
            .Where(row => !row.EventType.StartsWith("auth."))
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
            HouseholdName = "Unit Endpoints Household",
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
