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
/// HTTP-level verification of M2-10 proteins and tags (docs/API.md §22,
/// docs/DATABASE.md §7): CRUD, normalized-name conflicts with
/// <c>existing_id</c>, search/limit/autocomplete behavior, protein retire
/// idempotence, tags without a retire concept, Admin-only hard deletes through
/// the usage seam, the route permission matrix, and audit coverage for every
/// write. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class ProteinTagEndpointsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string ProteinsPath = "/api/v1/proteins";
    private const string TagsPath = "/api/v1/tags";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "protein-tag-endpoints-test";

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
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var proteinCreated = await CreateProteinAsync(session, """{"name":"  Chicken  "}""", cancellationToken);
        proteinCreated.StatusCode.Should().Be(HttpStatusCode.Created);
        var proteinRoot = await ReadJsonAsync(proteinCreated, cancellationToken);
        var proteinId = proteinRoot.GetProperty("id").GetInt64();
        proteinRoot.GetProperty("name").GetString().Should().Be("Chicken");
        proteinRoot.GetProperty("is_active").GetBoolean().Should().BeTrue();
        proteinRoot.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("id", "name", "is_active");

        var tagCreated = await CreateTagAsync(session, """{"name":"  Spicy  "}""", cancellationToken);
        tagCreated.StatusCode.Should().Be(HttpStatusCode.Created);
        var tagRoot = await ReadJsonAsync(tagCreated, cancellationToken);
        var tagId = tagRoot.GetProperty("id").GetInt64();
        tagRoot.GetProperty("name").GetString().Should().Be("Spicy");
        tagRoot.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(new[] { "id", "name" }, "tags have no timestamps");

        var renamed = await PatchProteinAsync(session, proteinId, """{"name":"Poultry"}""", cancellationToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(renamed, cancellationToken)).GetProperty("name").GetString()
            .Should().Be("Poultry");

        var deactivated = await PatchProteinAsync(session, proteinId, """{"is_active":false}""", cancellationToken);
        deactivated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(deactivated, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        var reactivated = await PatchProteinAsync(session, proteinId, """{"is_active":true}""", cancellationToken);
        reactivated.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(reactivated, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeTrue();

        var retired = await RetireProteinAsync(session, proteinId, cancellationToken);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(retired, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        var retiredAgain = await RetireProteinAsync(session, proteinId, cancellationToken);
        retiredAgain.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(retiredAgain, cancellationToken)).GetProperty("is_active").GetBoolean()
            .Should().BeFalse();

        var tagRenamed = await PatchTagAsync(session, tagId, """{"name":"Hot"}""", cancellationToken);
        tagRenamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(tagRenamed, cancellationToken)).GetProperty("name").GetString()
            .Should().Be("Hot");

        (await DeleteAsync(session, $"{ProteinsPath}/{proteinId}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await DeleteAsync(session, $"{TagsPath}/{tagId}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            "created",
            "created",
            "updated",
            "updated",
            "updated",
            "retired",
            "updated",
            "deleted",
            "deleted");
        rows.Select(row => row.EntityType).Should().Equal(
            "protein",
            "tag",
            "protein",
            "protein",
            "protein",
            "protein",
            "tag",
            "protein",
            "tag");
        rows.Should().OnlyContain(row => row.ActorUserId == admin.Id);

        Snapshot(rows[3].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(rows[3].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        Snapshot(rows[5].BeforeJson!)["name"]!.GetValue<string>().Should().Be("Poultry");
        Snapshot(rows[5].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        rows[5].EventType.Should().Be(AuditEventTypes.Retired);
        rows[6].BeforeJson.Should().NotBeNull("the tag rename records the display form before");
        Snapshot(rows[6].BeforeJson!)["name"]!.GetValue<string>().Should().Be("Spicy");
        rows[7].BeforeJson.Should().NotBeNull("the delete records the row before removal");
        rows[7].AfterJson.Should().BeNull();
    }

    [Fact]
    public async Task Create_conflicts_return_exists_with_existing_id_for_both_resources()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var protein = await CreateProteinAsync(session, """{"name":"Chicken"}""", cancellationToken);
        protein.StatusCode.Should().Be(HttpStatusCode.Created);
        var proteinId = (await ReadJsonAsync(protein, cancellationToken)).GetProperty("id").GetInt64();

        foreach (var json in new[]
                 {
                     """{"name":"chicken"}""",
                     """{"name":"  CHICKEN  "}""",
                     """{"name":"Chícken"}""",
                 })
        {
            var conflict = await CreateProteinAsync(session, json, cancellationToken);
            conflict.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                "'{0}' conflicts with the existing 'Chicken' protein",
                json);
            var error = await ReadErrorAsync(conflict, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ProteinExists);
            error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(proteinId);
        }

        var tag = await CreateTagAsync(session, """{"name":"Spicy"}""", cancellationToken);
        tag.StatusCode.Should().Be(HttpStatusCode.Created);
        var tagId = (await ReadJsonAsync(tag, cancellationToken)).GetProperty("id").GetInt64();

        foreach (var json in new[]
                 {
                     """{"name":"spicy"}""",
                     """{"name":"  SPICY  "}""",
                     """{"name":"Spícy"}""",
                 })
        {
            var conflict = await CreateTagAsync(session, json, cancellationToken);
            conflict.StatusCode.Should().Be(
                HttpStatusCode.Conflict,
                "'{0}' conflicts with the existing 'Spicy' tag",
                json);
            var error = await ReadErrorAsync(conflict, cancellationToken);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.TagExists);
            error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(tagId);
        }

        (await ReadAuditRowsAsync(cancellationToken)).Should().HaveCount(
            2,
            "the folded-name conflicts write no audit row");
    }

    [Fact]
    public async Task Patch_rename_conflicts_and_self_exclusion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var chicken = await CreateProteinAsync(session, """{"name":"Chicken"}""", cancellationToken);
        var chickenId = (await ReadJsonAsync(chicken, cancellationToken)).GetProperty("id").GetInt64();
        var beef = await CreateProteinAsync(session, """{"name":"Beef"}""", cancellationToken);
        var beefId = (await ReadJsonAsync(beef, cancellationToken)).GetProperty("id").GetInt64();

        var proteinConflict = await PatchProteinAsync(
            session,
            beefId,
            """{"name":" CHÍCKEN "}""",
            cancellationToken);
        proteinConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var proteinError = await ReadErrorAsync(proteinConflict, cancellationToken);
        proteinError.GetProperty("code").GetString().Should().Be(ErrorCodes.ProteinExists);
        proteinError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(chickenId);

        var selfRename = await PatchProteinAsync(session, chickenId, """{"name":"chicken"}""", cancellationToken);
        selfRename.StatusCode.Should().Be(HttpStatusCode.OK, "a no-op rename excludes itself");
        var renamedProtein = await PatchProteinAsync(session, beefId, """{"name":"Beef Stew"}""", cancellationToken);
        renamedProtein.StatusCode.Should().Be(HttpStatusCode.OK);

        var spicy = await CreateTagAsync(session, """{"name":"Spicy"}""", cancellationToken);
        var spicyId = (await ReadJsonAsync(spicy, cancellationToken)).GetProperty("id").GetInt64();
        var mild = await CreateTagAsync(session, """{"name":"Mild"}""", cancellationToken);
        var mildId = (await ReadJsonAsync(mild, cancellationToken)).GetProperty("id").GetInt64();

        var tagConflict = await PatchTagAsync(session, mildId, """{"name":" spícy "}""", cancellationToken);
        tagConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var tagError = await ReadErrorAsync(tagConflict, cancellationToken);
        tagError.GetProperty("code").GetString().Should().Be(ErrorCodes.TagExists);
        tagError.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(spicyId);

        (await PatchTagAsync(session, spicyId, """{"name":"Spicy"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await PatchTagAsync(session, mildId, """{"name":"Mild Curry"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => row.EventType).Should().Equal(
            "created",
            "created",
            "updated",
            "updated",
            "created",
            "created",
            "updated",
            "updated");
        rows.Count(row => row.EventType == AuditEventTypes.Updated).Should().Be(
            4,
            "the two failed renames write no audit row; the two self-renames are successful writes");
    }

    [Fact]
    public async Task Search_prefers_prefix_matches_and_applies_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        // The substring match gets the lower id so prefix ranking cannot pass
        // merely because of insertion order.
        var blackBean = await CreateProteinAsync(session, """{"name":"Black Bean"}""", cancellationToken);
        var blackBeanId = (await ReadJsonAsync(blackBean, cancellationToken)).GetProperty("id").GetInt64();
        var bean = await CreateProteinAsync(session, """{"name":"Bean"}""", cancellationToken);
        var beanId = (await ReadJsonAsync(bean, cancellationToken)).GetProperty("id").GetInt64();
        beanId.Should().BeGreaterThan(blackBeanId);

        var proteinSearch = await ReadJsonAsync(
            await session.Client.GetAsync($"{ProteinsPath}?search=bean", cancellationToken),
            cancellationToken);
        proteinSearch.GetArrayLength().Should().Be(2);
        proteinSearch[0].GetProperty("id").GetInt64().Should().Be(beanId, "prefix matches rank first");
        proteinSearch[1].GetProperty("id").GetInt64().Should().Be(blackBeanId);

        var verySpicy = await CreateTagAsync(session, """{"name":"Very Spicy"}""", cancellationToken);
        var verySpicyId = (await ReadJsonAsync(verySpicy, cancellationToken)).GetProperty("id").GetInt64();
        var spicy = await CreateTagAsync(session, """{"name":"Spicy"}""", cancellationToken);
        var spicyId = (await ReadJsonAsync(spicy, cancellationToken)).GetProperty("id").GetInt64();
        spicyId.Should().BeGreaterThan(verySpicyId);

        var tagSearch = await ReadJsonAsync(
            await session.Client.GetAsync($"{TagsPath}?search=spicy", cancellationToken),
            cancellationToken);
        tagSearch.GetArrayLength().Should().Be(2);
        tagSearch[0].GetProperty("id").GetInt64().Should().Be(spicyId, "prefix matches rank first");
        tagSearch[1].GetProperty("id").GetInt64().Should().Be(verySpicyId);

        await SeedProteinRangeAsync(105, cancellationToken);
        await SeedTagRangeAsync(25, cancellationToken);

        (await ReadJsonAsync(
                await session.Client.GetAsync(ProteinsPath, cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(20, "limit defaults to 20");
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{ProteinsPath}?limit=3", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(3);
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{ProteinsPath}?limit=1000", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(100, "limit clamps to 100");

        (await ReadJsonAsync(
                await session.Client.GetAsync(TagsPath, cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(20, "limit defaults to 20");
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{TagsPath}?limit=5", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(5);
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{TagsPath}?limit=1000", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(27, "all 27 tags fit under the 100 clamp");
    }

    [Fact]
    public async Task Retired_proteins_excluded_unless_requested_and_tags_have_no_retire()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var chicken = await SeedProteinAsync("Chicken", cancellationToken: cancellationToken);
        var beef = await SeedProteinAsync("Beef", isActive: false, cancellationToken);
        var spicy = await SeedTagAsync("Spicy", cancellationToken);
        await SeedTagAsync("Hot", cancellationToken);

        var activeProteins = await ReadJsonAsync(
            await session.Client.GetAsync(ProteinsPath, cancellationToken),
            cancellationToken);
        activeProteins.GetArrayLength().Should().Be(1);
        activeProteins[0].GetProperty("id").GetInt64().Should().Be(chicken.Id);

        var allProteins = await ReadJsonAsync(
            await session.Client.GetAsync($"{ProteinsPath}?include_retired=true", cancellationToken),
            cancellationToken);
        allProteins.GetArrayLength().Should().Be(2);
        allProteins.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray()
            .Should().BeEquivalentTo(new[] { chicken.Id, beef.Id });

        // Tags accept include_retired for contract uniformity but have no
        // retired concept, so the value never changes the result.
        (await ReadJsonAsync(
                await session.Client.GetAsync(TagsPath, cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(2);
        (await ReadJsonAsync(
                await session.Client.GetAsync($"{TagsPath}?include_retired=true", cancellationToken),
                cancellationToken))
            .GetArrayLength().Should().Be(2);

        var tagRetire = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{TagsPath}/{spicy.Id}/retire",
            json: null,
            session.CsrfToken,
            cancellationToken);
        tagRetire.StatusCode.Should().Be(HttpStatusCode.NotFound, "tags have no retire route");

        var tagResponse = await ReadJsonAsync(
            await session.Client.GetAsync($"{TagsPath}?search=spicy", cancellationToken),
            cancellationToken);
        tagResponse[0].TryGetProperty("is_active", out _).Should().BeFalse();
        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty("reads and unmatched routes write nothing");
    }

    [Fact]
    public async Task Delete_usage_seam_blocks_referenced_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        var checker = new CountingReferenceUsageChecker { ProteinCount = 3, TagCount = 2 };
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken, checker);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var protein = await SeedProteinAsync("Chicken", cancellationToken: cancellationToken);
        var tag = await SeedTagAsync("Spicy", cancellationToken);

        var blockedProtein = await DeleteAsync(session, $"{ProteinsPath}/{protein.Id}", cancellationToken);
        blockedProtein.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var proteinError = await ReadErrorAsync(blockedProtein, cancellationToken);
        proteinError.GetProperty("code").GetString().Should().Be(ErrorCodes.ProteinInUse);
        proteinError.GetProperty("details").GetProperty("usage_count").GetInt32().Should().Be(3);

        var blockedTag = await DeleteAsync(session, $"{TagsPath}/{tag.Id}", cancellationToken);
        blockedTag.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var tagError = await ReadErrorAsync(blockedTag, cancellationToken);
        tagError.GetProperty("code").GetString().Should().Be(ErrorCodes.TagInUse);
        tagError.GetProperty("details").GetProperty("usage_count").GetInt32().Should().Be(2);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty("blocked deletes write no audit row");

        checker.ProteinCount = 0;
        checker.TagCount = 0;
        (await DeleteAsync(session, $"{ProteinsPath}/{protein.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await DeleteAsync(session, $"{TagsPath}/{tag.Id}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Select(row => (row.EventType, row.EntityType)).Should().Equal(
            ("deleted", "protein"),
            ("deleted", "tag"));
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
        var seededProtein = await SeedProteinAsync("Seed Protein", cancellationToken: cancellationToken);
        var seededTag = await SeedTagAsync("Seed Tag", cancellationToken);
        using var session = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        (await session.Client.GetAsync(ProteinsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync(TagsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var protein = await CreateProteinAsync(session, """{"name":"Member Protein"}""", cancellationToken);
        protein.StatusCode.Should().Be(HttpStatusCode.Created);
        var proteinId = (await ReadJsonAsync(protein, cancellationToken)).GetProperty("id").GetInt64();
        (await PatchProteinAsync(session, proteinId, """{"name":"Member Renamed"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await RetireProteinAsync(session, proteinId, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var tag = await CreateTagAsync(session, """{"name":"Member Tag"}""", cancellationToken);
        tag.StatusCode.Should().Be(HttpStatusCode.Created);
        var tagId = (await ReadJsonAsync(tag, cancellationToken)).GetProperty("id").GetInt64();
        (await PatchTagAsync(session, tagId, """{"name":"Member Tag Renamed"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var deletedProtein = await DeleteAsync(session, $"{ProteinsPath}/{seededProtein.Id}", cancellationToken);
        deletedProtein.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorAsync(deletedProtein, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);
        var deletedTag = await DeleteAsync(session, $"{TagsPath}/{seededTag.Id}", cancellationToken);
        deletedTag.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorAsync(deletedTag, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(row => row.ActorUserId == jane.Id);
    }

    [Theory]
    [InlineData("GET", ProteinsPath, null, HttpStatusCode.OK)]
    [InlineData("POST", ProteinsPath, """{"name":"denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("PATCH", "/api/v1/proteins/1", """{"name":"Denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("POST", "/api/v1/proteins/1/retire", null, HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "/api/v1/proteins/1", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", TagsPath, null, HttpStatusCode.OK)]
    [InlineData("POST", TagsPath, """{"name":"denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("PATCH", "/api/v1/tags/1", """{"name":"Denied"}""", HttpStatusCode.Forbidden)]
    [InlineData("DELETE", "/api/v1/tags/1", null, HttpStatusCode.Forbidden)]
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
        anonymousResponse.StatusCode.Should().Be(
            HttpStatusCode.Unauthorized,
            "an anonymous actor performs {0} {1}",
            method,
            path);
        (await ReadErrorAsync(anonymousResponse, cancellationToken)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Single_user_system_actor_manages_proteins_and_tags()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var client = factory.CreateClient();

        (await client.GetAsync(ProteinsPath, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync(TagsPath, cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var protein = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ProteinsPath,
            """{"name":"System Protein"}""",
            csrfToken: null,
            cancellationToken);
        protein.StatusCode.Should().Be(HttpStatusCode.Created);
        var proteinId = (await ReadJsonAsync(protein, cancellationToken)).GetProperty("id").GetInt64();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ProteinsPath}/{proteinId}",
            """{"name":"System Renamed"}""",
            csrfToken: null,
            cancellationToken);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);

        var deactivated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ProteinsPath}/{proteinId}",
            """{"is_active":false}""",
            csrfToken: null,
            cancellationToken);
        deactivated.StatusCode.Should().Be(HttpStatusCode.OK);

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{ProteinsPath}/{proteinId}/retire",
            json: null,
            csrfToken: null,
            cancellationToken);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);

        var deletedProtein = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{ProteinsPath}/{proteinId}",
            json: null,
            csrfToken: null,
            cancellationToken);
        deletedProtein.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tag = await SendJsonAsync(
            client,
            HttpMethod.Post,
            TagsPath,
            """{"name":"System Tag"}""",
            csrfToken: null,
            cancellationToken);
        tag.StatusCode.Should().Be(HttpStatusCode.Created);
        var tagId = (await ReadJsonAsync(tag, cancellationToken)).GetProperty("id").GetInt64();

        var renamedTag = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{TagsPath}/{tagId}",
            """{"name":"System Tag Renamed"}""",
            csrfToken: null,
            cancellationToken);
        renamedTag.StatusCode.Should().Be(HttpStatusCode.OK);

        var deletedTag = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{TagsPath}/{tagId}",
            json: null,
            csrfToken: null,
            cancellationToken);
        deletedTag.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(cancellationToken);
        // The PATCH deactivation already retired the protein, so the following
        // retire is an idempotent no-op and writes no audit row.
        rows.Select(row => row.EventType).Should().Equal(
            "created",
            "updated",
            "updated",
            "deleted",
            "created",
            "updated",
            "deleted");
        rows.Should().OnlyContain(row => row.ActorUserId == null);
    }

    [Fact]
    public async Task Reads_and_failed_writes_write_no_audit_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        await SeedProteinAsync("Chicken", cancellationToken: cancellationToken);
        await SeedTagAsync("Spicy", cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        (await session.Client.GetAsync(ProteinsPath, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{TagsPath}?search=spicy&include_retired=true", cancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync($"{ProteinsPath}?limit=banana", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await session.Client.GetAsync($"{TagsPath}?include_retired=maybe", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        (await CreateProteinAsync(session, """{"name":"chicken"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await CreateTagAsync(session, """{"name":"spicy"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
        (await CreateProteinAsync(session, """{"name":"   "}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await CreateTagAsync(session, """{"name":5}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await PatchProteinAsync(session, 999_999, """{"name":"Ghost"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await PatchTagAsync(session, 999_999, """{"name":"Ghost"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await DeleteAsync(session, $"{ProteinsPath}/999999", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await DeleteAsync(session, $"{TagsPath}/999999", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await ReadAuditRowsAsync(cancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Invalid_bodies_and_query_values_return_validation_errors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        using var factory = await StartFactoryWithCleanReferenceDataAsync(cancellationToken);
        using var session = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        foreach (var query in new[] { "limit=0", "limit=-3", "limit=banana", "include_retired=maybe" })
        {
            var response = await session.Client.GetAsync($"{ProteinsPath}?{query}", cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' is invalid", query);
            (await ReadErrorAsync(response, cancellationToken)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.ValidationFailed);
        }

        (await session.Client.GetAsync($"{ProteinsPath}?limit=101", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK, "an over-limit value clamps instead of failing");

        foreach (var json in new[] { "{}", """{"name":5}""", """{"name":"   "}""" })
        {
            var create = await CreateProteinAsync(session, json, cancellationToken);
            create.StatusCode.Should().Be(HttpStatusCode.BadRequest, "'{0}' is invalid", json);
        }

        var arrayBody = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            ProteinsPath,
            "[]",
            session.CsrfToken,
            cancellationToken);
        arrayBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var protein = await CreateProteinAsync(session, """{"name":"Chicken"}""", cancellationToken);
        var proteinId = (await ReadJsonAsync(protein, cancellationToken)).GetProperty("id").GetInt64();
        (await PatchProteinAsync(session, proteinId, "{}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await PatchProteinAsync(session, proteinId, """{"name":5}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await PatchProteinAsync(session, proteinId, """{"is_active":"yes"}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var tag = await CreateTagAsync(session, """{"name":"Spicy"}""", cancellationToken);
        var tagId = (await ReadJsonAsync(tag, cancellationToken)).GetProperty("id").GetInt64();
        (await PatchTagAsync(session, tagId, "{}", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await PatchTagAsync(session, tagId, """{"name":5}""", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        (await ReadAuditRowsAsync(cancellationToken)).Should().HaveCount(
            2,
            "only the two successful creates are audited");
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

    private static Task<HttpResponseMessage> CreateProteinAsync(
        SignedInClient session,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session.Client, HttpMethod.Post, ProteinsPath, json, session.CsrfToken, cancellationToken);

    private static Task<HttpResponseMessage> PatchProteinAsync(
        SignedInClient session,
        long id,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{ProteinsPath}/{id}",
            json,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> RetireProteinAsync(
        SignedInClient session,
        long id,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{ProteinsPath}/{id}/retire",
            json: null,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> CreateTagAsync(
        SignedInClient session,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session.Client, HttpMethod.Post, TagsPath, json, session.CsrfToken, cancellationToken);

    private static Task<HttpResponseMessage> PatchTagAsync(
        SignedInClient session,
        long id,
        string json,
        CancellationToken cancellationToken) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{TagsPath}/{id}",
            json,
            session.CsrfToken,
            cancellationToken);

    private static Task<HttpResponseMessage> DeleteAsync(
        SignedInClient session,
        string path,
        CancellationToken cancellationToken) =>
        SendJsonAsync(session.Client, HttpMethod.Delete, path, json: null, session.CsrfToken, cancellationToken);

    /// <summary>
    /// Boots the host (which runs the M2-04 startup seeder), then clears
    /// proteins and tags so each test controls its own reference data. Pass a
    /// counting <see cref="IReferenceUsageChecker"/> fake to exercise the
    /// delete-usage seam; the default registration returns zero for every
    /// resource.
    /// </summary>
    private async Task<ApiFactory> StartFactoryWithCleanReferenceDataAsync(
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
            "DELETE FROM proteins; DELETE FROM tags;",
            cancellationToken);
        return factory;
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

    private async Task SeedProteinRangeAsync(int count, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        for (var index = 1; index <= count; index++)
        {
            var display = $"Bulk Protein {index:000}";
            var normalized = (ReferenceDataNormalizationResult.Success)
                ReferenceDataNormalizer.Normalize(display)!;
            context.Proteins.Add(new Protein
            {
                Name = normalized.Display,
                NormalizedName = normalized.Normalized,
                IsActive = true,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedTagRangeAsync(int count, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        for (var index = 1; index <= count; index++)
        {
            var display = $"Bulk Tag {index:000}";
            var normalized = (ReferenceDataNormalizationResult.Success)
                ReferenceDataNormalizer.Normalize(display)!;
            context.Tags.Add(new Tag
            {
                Name = normalized.Display,
                NormalizedName = normalized.Normalized,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();

        // M12-01 mirrors login/logout into audit_events; these suites assert the
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
            HouseholdName = "Protein Tag Endpoints Household",
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
