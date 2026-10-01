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
/// HTTP-level verification of M2-05 departments (docs/API.md §22): CRUD,
/// ordering/search/limit, retire and delete semantics, the complete-permutation
/// order update, the route permission matrix, and audit coverage for every
/// write. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class DepartmentEndpointsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string DepartmentsPath = "/api/v1/departments";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "department-endpoints-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Admin_crud_writes_audit_rows_with_before_and_after_snapshots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var created = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            DepartmentsPath,
            JsonSerializer.Serialize(new { name = "  Produceé  " }),
            session.CsrfToken,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created, cancellationToken);
        var id = createdRoot.GetProperty("id").GetInt64();
        createdRoot.GetProperty("name").GetString().Should().Be("Produceé");
        createdRoot.GetProperty("sort_order").GetInt32().Should().Be(1);
        createdRoot.GetProperty("is_active").GetBoolean().Should().BeTrue();

        var renamed = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{id}",
            """{"name":"Fresh Produce"}""",
            session.CsrfToken,
            cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(renamed, cancellationToken)).GetProperty("name").GetString()
            .Should().Be("Fresh Produce");

        var retired = await RetireAsync(session, id, cancellationToken);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(retired, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        (await ReadJsonAsync(
                await session.Client.GetAsync(DepartmentsPath, cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(0);
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{DepartmentsPath}?include_retired=true", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(1);

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{DepartmentsPath}/{id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{DepartmentsPath}?include_retired=true", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(0);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().HaveCount(4);
        rows.Select(row => row.EventType).Should().Equal("created", "updated", "retired", "deleted");
        rows.Should().OnlyContain(row => row.EntityType == AuditEntityTypes.Department);
        var entityId = id.ToString(CultureInfo.InvariantCulture);
        rows.Should().OnlyContain(row => row.EntityId == entityId);
        rows.Should().OnlyContain(row => row.ActorUserId == admin.Id);

        rows[0].BeforeJson.Should().BeNull();
        Snapshot(rows[0].AfterJson)["name"]!.GetValue<string>().Should().Be("Produceé");
        Snapshot(rows[1].BeforeJson!)["name"]!.GetValue<string>().Should().Be("Produceé");
        Snapshot(rows[1].AfterJson!)["name"]!.GetValue<string>().Should().Be("Fresh Produce");
        Snapshot(rows[2].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(rows[2].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        Snapshot(rows[3].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        rows[3].AfterJson.Should().BeNull();
    }

    [Fact]
    public async Task Duplicate_names_collide_across_case_whitespace_and_diacritics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await CreateDepartmentAsync(session, "Produce", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Created);

        foreach (var variant in new[] { " produce ", "PRODUCE", "PRÓDUCE", "Pröduce" })
        {
            var response = await CreateDepartmentAsync(session, variant, cancellationToken);
            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "'{0}' normalizes to the existing 'produce'",
                variant);
            var error = await ReadErrorAsync(response, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
            error.GetProperty("details").GetProperty("field_errors")
                .TryGetProperty("name", out _).Should().BeTrue();
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle()
            .Which.EventType.Should().Be("created");
    }

    [Fact]
    public async Task Rename_rejects_conflicts_and_updates_the_display_form()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var first = await CreateDepartmentAsync(session, "Produce", cancellationToken);
        var firstId = (await ReadJsonAsync(first, cancellationToken)).GetProperty("id").GetInt64();
        var second = await CreateDepartmentAsync(session, "Meat", cancellationToken);
        var secondId = (await ReadJsonAsync(second, cancellationToken)).GetProperty("id").GetInt64();

        var conflict = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{secondId}",
            """{"name":"PRODUCE"}""",
            session.CsrfToken,
            cancellationToken);
        conflict.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(conflict, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ValidationFailed);

        var renamed = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{secondId}",
            """{"name":"  Próteins "}""",
            session.CsrfToken,
            cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(renamed, cancellationToken)).GetProperty("name").GetString()
            .Should().Be("Próteins");

        await using var context = fixture.CreateContext();
        var stored = await context.GroceryDepartments.AsNoTracking()
            .SingleAsync(department => department.Id == secondId, cancellationToken);
        stored.NormalizedName.Should().Be("proteins");
        (await context.GroceryDepartments.AsNoTracking()
                .SingleAsync(department => department.Id == firstId, cancellationToken))
            .Name.Should().Be("Produce", "the failed rename must not touch the other department");
    }

    [Fact]
    public async Task Get_orders_by_sort_order_and_ranks_prefix_matches_first()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);

        var meat = await SeedDepartmentAsync("Meat", 1, cancellationToken: cancellationToken);
        var organic = await SeedDepartmentAsync("Organic Produce", 2, cancellationToken: cancellationToken);
        var produce = await SeedDepartmentAsync("Produce", 3, cancellationToken: cancellationToken);
        var frozen = await SeedDepartmentAsync("Frozen Produce", 4, cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var all = await ReadJsonAsync(
            await session.Client.GetAsync(DepartmentsPath, cancellationToken),
            cancellationToken);
        all.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(meat.Id, organic.Id, produce.Id, frozen.Id);

        var searched = await ReadJsonAsync(
            await session.Client.GetAsync($"{DepartmentsPath}?search=produce", cancellationToken),
            cancellationToken);
        searched.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(
                new[] { produce.Id, organic.Id, frozen.Id },
                "prefix matches rank before substring matches");

        var upper = await ReadJsonAsync(
            await session.Client.GetAsync($"{DepartmentsPath}?search=PRODUCE", cancellationToken),
            cancellationToken);
        upper.EnumerateArray().Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(produce.Id, organic.Id, frozen.Id);

        (await ReadJsonAsync(
                await session.Client.GetAsync($"{DepartmentsPath}?search=organic", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Get_honors_include_retired_and_clamps_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);

        for (var index = 1; index <= 25; index++)
        {
            await SeedDepartmentAsync($"Bulk {index:00}", index, cancellationToken: cancellationToken);
        }

        await SeedDepartmentAsync(
            "Bulk Retired",
            26,
            isActive: false,
            cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var defaults = await ReadJsonAsync(
            await session.Client.GetAsync(DepartmentsPath, cancellationToken),
            cancellationToken);
        defaults.GetArrayLength().Should().Be(20, "limit defaults to 20");
        defaults.EnumerateArray().First().GetProperty("name").GetString().Should().Be("Bulk 01");
        defaults.EnumerateArray().Last().GetProperty("name").GetString().Should().Be("Bulk 20");

        var clamped = await ReadJsonAsync(
            await session.Client.GetAsync($"{DepartmentsPath}?limit=1000", cancellationToken),
            cancellationToken);
        clamped.GetArrayLength().Should().Be(25, "the limit is clamped to 100, not exceeded");

        var withRetired = await ReadJsonAsync(
            await session.Client.GetAsync($"{DepartmentsPath}?include_retired=true&limit=1000", cancellationToken),
            cancellationToken);
        withRetired.GetArrayLength().Should().Be(26);

        foreach (var query in new[] { "?limit=0", "?limit=banana" })
        {
            var invalid = await session.Client.GetAsync($"{DepartmentsPath}{query}", cancellationToken);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorAsync(invalid, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty("limit", out _).Should().BeTrue();
        }

        var invalidRetired = await session.Client.GetAsync(
            $"{DepartmentsPath}?include_retired=maybe",
            cancellationToken);
        invalidRetired.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(invalidRetired, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("include_retired", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Retire_is_idempotent_and_writes_a_single_audit_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        var department = await SeedDepartmentAsync("Retired Once", 1, cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await RetireAsync(session, department.Id, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        var second = await RetireAsync(session, department.Id, cancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(second, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle()
            .Which.EventType.Should().Be("retired");
    }

    [Fact]
    public async Task Patch_is_active_toggle_is_audited_as_updated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        var department = await SeedDepartmentAsync("Toggle Me", 1, cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var deactivated = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{department.Id}",
            """{"is_active":false}""",
            session.CsrfToken,
            cancellationToken);
        deactivated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(deactivated, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        var reactivated = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{department.Id}",
            """{"is_active":true}""",
            session.CsrfToken,
            cancellationToken);
        reactivated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(reactivated, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeTrue();

        var nonBoolean = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{department.Id}",
            """{"is_active":"no"}""",
            session.CsrfToken,
            cancellationToken);
        nonBoolean.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(nonBoolean, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("is_active", out _).Should().BeTrue();

        var empty = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{department.Id}",
            "{}",
            session.CsrfToken,
            cancellationToken);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(empty, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("body", out _).Should().BeTrue();

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row => row.EventType == AuditEventTypes.Updated);
    }

    [Fact]
    public async Task Unknown_department_ids_return_not_found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var requests = new (HttpMethod Method, string Path, string? Json)[]
        {
            (HttpMethod.Patch, $"{DepartmentsPath}/999999", """{"name":"Nothing"}"""),
            (HttpMethod.Post, $"{DepartmentsPath}/999999/retire", null),
            (HttpMethod.Delete, $"{DepartmentsPath}/999999", null),
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
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0} {1}", method, path);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.NotFound);
        }
    }

    [Fact]
    public async Task Delete_is_blocked_while_ingredients_reference_the_department()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        var department = await SeedDepartmentAsync("In-Use Department", 1, cancellationToken: cancellationToken);
        await SeedIngredientAsync("Tomato", department.Id, cancellationToken);
        await SeedIngredientAsync("Lettuce", department.Id, cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var blocked = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{DepartmentsPath}/{department.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await ReadErrorAsync(blocked, cancellationToken);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.DepartmentInUse);
        error.GetProperty("details").GetProperty("ingredient_count").GetInt32().Should().Be(2);

        await using (var context = fixture.CreateContext())
        {
            (await context.GroceryDepartments.AsNoTracking()
                    .AnyAsync(candidate => candidate.Id == department.Id, cancellationToken))
                .Should().BeTrue("a blocked delete must leave the department in place");
            await context.Database.ExecuteSqlRawAsync("DELETE FROM ingredients;", cancellationToken);
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{DepartmentsPath}/{department.Id}",
            json: null,
            session.CsrfToken,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().ContainSingle().Which.EventType.Should().Be("deleted");
        Snapshot(rows[0].BeforeJson!)["name"]!.GetValue<string>().Should().Be("In-Use Department");
    }

    [Fact]
    public async Task Order_update_requires_a_complete_permutation_and_audits_the_change()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);

        var first = await SeedDepartmentAsync("First", 1, cancellationToken: cancellationToken);
        var second = await SeedDepartmentAsync("Second", 2, cancellationToken: cancellationToken);
        var third = await SeedDepartmentAsync("Third", 3, cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var reordered = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{DepartmentsPath}/order",
            JsonSerializer.Serialize(new { ids = new[] { third.Id, first.Id, second.Id } }),
            session.CsrfToken,
            cancellationToken);
        reordered.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(reordered, cancellationToken)).EnumerateArray()
            .Select(element => element.GetProperty("id").GetInt64())
            .Should().Equal(third.Id, first.Id, second.Id);

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.GroceryDepartments.AsNoTracking()
                .OrderBy(department => department.SortOrder)
                .Select(department => new { department.Id, department.SortOrder })
                .ToListAsync(cancellationToken);
            stored.Select(row => row.Id).Should().Equal(third.Id, first.Id, second.Id);
            stored.Select(row => row.SortOrder).Should().Equal(1, 2, 3);
        }

        var audit = (await ReadAuditRowsAsync(cancellationToken)).Should().ContainSingle().Subject;
        audit.EventType.Should().Be(AuditEventTypes.Reordered);
        audit.EntityType.Should().Be(AuditEntityTypes.Department);
        audit.EntityId.Should().BeNull();
        JsonNode.DeepEquals(
                Snapshot(audit.BeforeJson!),
                JsonNode.Parse(JsonSerializer.Serialize(new[] { first.Id, second.Id, third.Id })))
            .Should().BeTrue();
        JsonNode.DeepEquals(
                Snapshot(audit.AfterJson!),
                JsonNode.Parse(JsonSerializer.Serialize(new[] { third.Id, first.Id, second.Id })))
            .Should().BeTrue();

        var failures = new[]
        {
            JsonSerializer.Serialize(new { ids = new[] { third.Id, first.Id } }),
            JsonSerializer.Serialize(new { ids = new[] { third.Id, first.Id, second.Id, 999999L } }),
            JsonSerializer.Serialize(new { ids = new[] { third.Id, first.Id, first.Id } }),
            """{"ids":3}""",
            "{}",
        };

        foreach (var failure in failures)
        {
            var response = await SendJsonAsync(
                session.Client,
                HttpMethod.Put,
                $"{DepartmentsPath}/order",
                failure,
                session.CsrfToken,
                cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} must fail", failure);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.ValidationFailed);
        }

        await RetireAsync(session, second.Id, cancellationToken);
        var missingRetired = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{DepartmentsPath}/order",
            JsonSerializer.Serialize(new { ids = new[] { third.Id, first.Id } }),
            session.CsrfToken,
            cancellationToken);
        missingRetired.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            "retired departments remain part of the required permutation");

        var withRetired = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{DepartmentsPath}/order",
            JsonSerializer.Serialize(new { ids = new[] { second.Id, third.Id, first.Id } }),
            session.CsrfToken,
            cancellationToken);
        withRetired.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadAuditRowsAsync(cancellationToken))
            .Count(row => row.EventType == AuditEventTypes.Reordered)
            .Should().Be(2, "failed permutations write no audit row");
    }

    [Fact]
    public async Task Reads_write_no_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        await SeedDepartmentAsync("Read Only", 1, cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        (await session.Client.GetAsync(DepartmentsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync(
                $"{DepartmentsPath}?search=read&include_retired=true",
                cancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Malformed_and_invalid_bodies_return_validation_errors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var malformed = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            DepartmentsPath,
            "not json",
            session.CsrfToken,
            cancellationToken);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(malformed, cancellationToken)).GetProperty("details")
            .GetProperty("field_errors").TryGetProperty("body", out _).Should().BeTrue();

        foreach (var json in new[] { "{}", """{"name":5}""" })
        {
            var response = await SendJsonAsync(
                session.Client,
                HttpMethod.Post,
                DepartmentsPath,
                json,
                session.CsrfToken,
                cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("details")
                .GetProperty("field_errors").TryGetProperty("name", out _).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData("GET", DepartmentsPath, null, HttpStatusCode.OK)]
    [InlineData("POST", DepartmentsPath, """{"name":"Denied Create"}""", HttpStatusCode.Forbidden)]
    [InlineData("PATCH", "/api/v1/departments/1", """{"name":"Denied Rename"}""", HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/v1/departments/1/retire", null, HttpStatusCode.Forbidden)]
    [InlineData("PUT", "/api/v1/departments/order", """{"ids":[]}""", HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "/api/v1/departments/1", null, HttpStatusCode.Forbidden)]
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
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
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
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        var seeded = await SeedDepartmentAsync("Seed Department", 1, cancellationToken: cancellationToken);

        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        (await session.Client.GetAsync(DepartmentsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var created = await CreateDepartmentAsync(session, "Member Created", cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdId = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        var renamed = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{createdId}",
            """{"name":"Member Renamed"}""",
            session.CsrfToken,
            cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var reordered = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{DepartmentsPath}/order",
            JsonSerializer.Serialize(new { ids = new[] { createdId, seeded.Id } }),
            session.CsrfToken,
            cancellationToken);
        reordered.StatusCode.Should().Be(HttpStatusCode.OK);

        (await RetireAsync(session, createdId, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{DepartmentsPath}/{seeded.Id}",
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
    public async Task Single_user_system_actor_manages_departments_without_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        var seeded = await SeedDepartmentAsync("System Seed", 1, cancellationToken: cancellationToken);

        using var client = factory.CreateClient();

        (await client.GetAsync(DepartmentsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            DepartmentsPath,
            """{"name":"System Created"}""",
            csrfToken: null,
            cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdId = (await ReadJsonAsync(created, cancellationToken)).GetProperty("id").GetInt64();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{createdId}",
            """{"name":"System Renamed","is_active":false}""",
            csrfToken: null,
            cancellationToken);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var updatedRoot = await ReadJsonAsync(updated, cancellationToken);
        updatedRoot.GetProperty("name").GetString().Should().Be("System Renamed");
        updatedRoot.GetProperty("is_active").GetBoolean().Should().BeFalse();

        var reordered = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{DepartmentsPath}/order",
            JsonSerializer.Serialize(new { ids = new[] { createdId, seeded.Id } }),
            csrfToken: null,
            cancellationToken);
        reordered.StatusCode.Should().Be(HttpStatusCode.OK);

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{DepartmentsPath}/{seeded.Id}/retire",
            json: null,
            csrfToken: null,
            cancellationToken);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{DepartmentsPath}/{createdId}",
            json: null,
            csrfToken: null,
            cancellationToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType)
            .Should().Equal("created", "updated", "reordered", "retired", "deleted");
        rows.Should().OnlyContain(row => row.ActorUserId == null);
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

    private static Task<HttpResponseMessage> CreateDepartmentAsync(
        SignedInClient session,
        string name,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            DepartmentsPath,
            JsonSerializer.Serialize(new { name }),
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> RetireAsync(
        SignedInClient session,
        long id,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{DepartmentsPath}/{id}/retire",
            json: null,
            session.CsrfToken,
            cancellationToken);

    /// <summary>
    /// Boots the host (which runs the M2-04 startup seeder) and then clears
    /// ingredients and departments so each test controls its own reference data.
    /// </summary>
    private async Task<ApiFactory> StartFactoryWithCleanReferenceDataAsync(
        CancellationToken cancellationToken)
    {
        var factory = fixture.CreateFactory();
        using (factory.CreateClient())
        {
        }

        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM ingredients; DELETE FROM grocery_departments;",
            cancellationToken);
        return factory;
    }

    private async Task<GroceryDepartment> SeedDepartmentAsync(
        string name,
        int sortOrder,
        bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var department = new GroceryDepartment
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            SortOrder = sortOrder,
            IsActive = isActive,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.GroceryDepartments.Add(department);
        await context.SaveChangesAsync(cancellationToken);
        return department;
    }

    private async Task SeedIngredientAsync(
        string name,
        long departmentId,
        CancellationToken cancellationToken)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        context.Ingredients.Add(new Ingredient
        {
            CanonicalName = normalized.Display,
            NormalizedName = normalized.Normalized,
            DepartmentId = departmentId,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });

        await context.SaveChangesAsync(cancellationToken);
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
            HouseholdName = "Department Endpoints Household",
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
