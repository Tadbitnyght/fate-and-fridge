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
/// HTTP-level verification of the staple catalog and the check-state
/// routes: CRUD, active-only listing, the
/// duplicate-name conflict, reference-data validation, ingredient merge
/// redirect, department defaulting, name/quantity rules, add-to-list range
/// scoping and non-mutation, the check/uncheck round trip, idempotency
/// household sharing, quantity-independent identity, dimension separation
/// ordering, and the permission matrix. The clock is injected so "today" is
/// deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(GroceryListCollection.Name)]
public sealed class StapleAndCheckStateIntegrationTests(GroceryListFixture fixture) : IAsyncLifetime
{
    private const string GroceriesPath = "/api/v1/groceries";
    private const string StaplesPath = "/api/v1/groceries/staples";
    private const string CheckPath = "/api/v1/groceries/check";
    private const string UncheckPath = "/api/v1/groceries/uncheck";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "grocery-staple-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date, and comfortably
    // after the real host date so an injected clock never expires a session cookie.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync(Ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // Staples: create / read / patch

    [Fact]
    public async Task Staple_create_read_patch_round_trip()
    {
        var lb = await SeedUnitAsync("lb", "pound", UnitDimension.Mass, 453.59237m);
        var target = await SeedIngredientAsync("M8 S Roundtrip Ingredient", departmentId: null);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("  M8 S Roundtrip  ", ingredientId: target.Id, quantity: 2m, unitId: lb.Id),
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var staple = await ReadJsonAsync(created);
        var id = staple.GetProperty("id").GetInt64();
        id.Should().BeGreaterThan(0);
        staple.GetProperty("name").GetString().Should().Be("M8 S Roundtrip");
        staple.GetProperty("ingredient_id").GetInt64().Should().Be(target.Id);
        staple.GetProperty("department_id").ValueKind.Should().Be(JsonValueKind.Null);
        staple.GetProperty("default_quantity_numeric").GetDecimal().Should().Be(2m);
        staple.GetProperty("unit_id").GetInt64().Should().Be(lb.Id);
        staple.GetProperty("is_active").GetBoolean().Should().BeTrue();
        staple.GetProperty("created_at").GetString().Should().NotBeNullOrEmpty();
        staple.GetProperty("updated_at").GetString().Should().NotBeNullOrEmpty();

        var listed = await GetStaplesAsync(client);
        listed.Should().ContainSingle(document => document.GetProperty("id").GetInt64() == id);

        var patched = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/{id}",
            """{"name":"M8 S Roundtrip Renamed","unit_id":null,"default_quantity_numeric":null}""",
            csrfToken: null);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterPatch = await ReadJsonAsync(patched);
        afterPatch.GetProperty("name").GetString().Should().Be("M8 S Roundtrip Renamed");
        afterPatch.GetProperty("unit_id").ValueKind.Should().Be(JsonValueKind.Null);
        afterPatch.GetProperty("default_quantity_numeric").ValueKind.Should().Be(JsonValueKind.Null);
        afterPatch.GetProperty("ingredient_id").GetInt64().Should().Be(target.Id);
    }

    [Fact]
    public async Task Staples_list_defaults_to_active_and_include_inactive_reveals_retired()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var activeId = await CreateStapleAsync(client, "M8 S Active Staple");
        var retiredId = await CreateStapleAsync(client, "M8 S Retired Staple");
        (await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/{retiredId}",
            """{"is_active":false}""",
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var activeOnly = await GetStaplesAsync(client);
        activeOnly.Select(staple => staple.GetProperty("id").GetInt64())
            .Should().Contain(activeId).And.NotContain(retiredId);

        var withInactive = await GetStaplesAsync(client, "?include_inactive=true");
        withInactive.Select(staple => staple.GetProperty("id").GetInt64())
            .Should().Contain([activeId, retiredId]);
    }

    [Fact]
    public async Task Staples_list_rejects_an_invalid_include_inactive()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{StaplesPath}?include_inactive=maybe", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("include_inactive", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Staples_are_ordered_by_name_case_insensitively()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        await CreateStapleAsync(client, "M8 S banana");
        await CreateStapleAsync(client, "M8 S Apple");
        await CreateStapleAsync(client, "M8 S cherry");

        var names = (await GetStaplesAsync(client))
            .Select(staple => staple.GetProperty("name").GetString())
            .Where(name => name!.StartsWith("M8 S", StringComparison.Ordinal))
            .ToArray();
        names.Should().Equal("M8 S Apple", "M8 S banana", "M8 S cherry");
    }

    [Fact]
    public async Task Staples_duplicate_active_name_is_rejected_then_allowed_after_retiring()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var firstId = await CreateStapleAsync(client, "M8 S Duplicate");

        // A different casing and whitespace collapses onto the same normalized key.
        var duplicate = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("  m8   s   duplicate "),
            csrfToken: null);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await ReadErrorAsync(duplicate);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.GroceryStapleExists);
        error.GetProperty("details").GetProperty("existing_id").GetInt64().Should().Be(firstId);

        // Retiring the active staple removes the conflict.
        (await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/{firstId}",
            """{"is_active":false}""",
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("M8 S Duplicate"),
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("unit_id")]
    [InlineData("department_id")]
    [InlineData("ingredient_id")]
    public async Task Staple_create_reports_unknown_reference_data(string field)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var payload = new Dictionary<string, object?> { ["name"] = "M8 S Bad Reference" };
        payload[field] = 999999L;

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            JsonSerializer.Serialize(payload),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var fieldErrors = (await ReadErrorAsync(response))
            .GetProperty("details").GetProperty("field_errors");
        fieldErrors.TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Staple_ingredient_is_canonicalized_through_a_merge_redirect()
    {
        var target = await SeedIngredientAsync("M8 S Canonical Onion", departmentId: null);
        var source = await SeedIngredientAsync("M8 S Merged Scallion", departmentId: null);
        await SeedMergeAsync(source.Id, target.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("M8 S Merged Staple", ingredientId: source.Id),
            csrfToken: null);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(created)).GetProperty("ingredient_id").GetInt64().Should().Be(target.Id);
    }

    [Fact]
    public async Task Staple_department_defaults_from_the_linked_ingredient()
    {
        var produce = await SeedDepartmentAsync("M8 S Produce", sortOrder: 1);
        var bakery = await SeedDepartmentAsync("M8 S Bakery", sortOrder: 2);
        var carrots = await SeedIngredientAsync("M8 S Carrots", produce.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var defaulted = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("M8 S Defaulted Dept", ingredientId: carrots.Id),
            csrfToken: null);
        (await ReadJsonAsync(defaulted)).GetProperty("department_id").GetInt64().Should().Be(produce.Id);

        var explicitDept = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("M8 S Explicit Dept", ingredientId: carrots.Id, departmentId: bakery.Id),
            csrfToken: null);
        (await ReadJsonAsync(explicitDept)).GetProperty("department_id").GetInt64().Should().Be(bakery.Id);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Staple_default_quantity_must_be_positive(string quantity)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            $$"""{"name":"M8 S Bad Quantity","default_quantity_numeric":{{quantity}}}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("default_quantity_numeric", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("   ", "name")]
    [InlineData("M8 S Bad\u0007Name", "name")]
    [InlineData("__LONG__", "name")]
    public async Task Staple_name_rules_are_enforced(string name, string field)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var value = name == "__LONG__" ? new string('x', 201) : name;
        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            JsonSerializer.Serialize(new { name = value }),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("details").GetProperty("field_errors")
            .TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Staple_patch_missing_id_is_not_found()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/999999",
            """{"name":"M8 S Missing"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(response)).GetProperty("code").GetString().Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Staple_patch_cannot_clear_the_name_and_rejects_an_empty_body()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var id = await CreateStapleAsync(client, "M8 S Patch Name");

        foreach (var json in new[] { """{"name":null}""", """{"name":""}""" })
        {
            var response = await SendJsonAsync(
                client,
                HttpMethod.Patch,
                $"{StaplesPath}/{id}",
                json,
                csrfToken: null);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var error = await ReadErrorAsync(response);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
            error.GetProperty("details").GetProperty("field_errors")
                .TryGetProperty("name", out _).Should().BeTrue();
        }

        var empty = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/{id}",
            """{}""",
            csrfToken: null);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var emptyError = await ReadErrorAsync(empty);
        emptyError.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        emptyError.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("body", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Staple_patch_duplicate_name_is_rejected_excluding_itself()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var first = await CreateStapleAsync(client, "M8 S Conflict One");
        var second = await CreateStapleAsync(client, "M8 S Conflict Two");

        // Renaming to its own (unchanged) name is not a conflict.
        (await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/{first}",
            """{"name":"M8 S Conflict One"}""",
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var conflict = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/{second}",
            """{"name":"M8 S Conflict One"}""",
            csrfToken: null);
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(conflict)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.GroceryStapleExists);
    }

    // Staples: add to list

    [Fact]
    public async Task Staple_add_to_list_creates_a_range_scoped_manual_item_without_mutating_the_staple()
    {
        var lb = await SeedUnitAsync("lb", "pound", UnitDimension.Mass, 453.59237m);
        var produce = await SeedDepartmentAsync("M8 S Add Produce", sortOrder: 1);
        var carrots = await SeedIngredientAsync("M8 S Add Carrots", produce.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var stapleId = await CreateStapleAsync(
            client,
            "M8 S Add Carrots Bag",
            ingredientId: carrots.Id,
            quantity: 3m,
            unitId: lb.Id);

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{StaplesPath}/{stapleId}/add-to-list",
            JsonSerializer.Serialize(new { range_start = Format(Today), range_days = 7 }),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = await ReadJsonAsync(response);
        var manualItemId = item.GetProperty("id").GetInt64();
        item.GetProperty("name").GetString().Should().Be("M8 S Add Carrots Bag");
        item.GetProperty("ingredient_id").GetInt64().Should().Be(carrots.Id);
        item.GetProperty("department_id").GetInt64().Should().Be(produce.Id);
        item.GetProperty("quantity_numeric").GetDecimal().Should().Be(3m);
        item.GetProperty("unit_id").GetInt64().Should().Be(lb.Id);
        item.GetProperty("range_start").GetString().Should().Be(Format(Today));
        item.GetProperty("range_days").GetInt32().Should().Be(7);
        item.GetProperty("save_for_future").GetBoolean().Should().BeFalse();

        // The manual item shows only for the exact active range.
        var withinRange = await GetRootAsync(
            client,
            $"{GroceriesPath}?start={Format(Today)}&days=7");
        var manualLine = ItemsNamed(withinRange, "M8 S Add Carrots Bag").Single();
        manualLine.GetProperty("source_type").GetString().Should().Be("manual");
        manualLine.GetProperty("manual_item_id").GetInt64().Should().Be(manualItemId);
        manualLine.GetProperty("quantity").GetDecimal().Should().Be(3m);

        var otherRange = await GetRootAsync(client, $"{GroceriesPath}?preset=next_14_days");
        ItemsNamed(otherRange, "M8 S Add Carrots Bag").Should().BeEmpty();

        // The staple is neither duplicated nor mutated.
        (await CountStaplesAsync()).Should().Be(1);
        var staple = await GetStapleAsync(stapleId);
        staple.Should().NotBeNull();
        staple!.Name.Should().Be("M8 S Add Carrots Bag");
        staple.IngredientId.Should().Be(carrots.Id);
        staple.DepartmentId.Should().Be(produce.Id);
        staple.DefaultQuantityNumeric.Should().Be(3m);
        staple.UnitId.Should().Be(lb.Id);
        staple.IsActive.Should().BeTrue();
        (await CountManualItemsAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Staple_add_to_list_missing_or_inactive_staple_is_not_found()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var rangeJson = JsonSerializer.Serialize(new { range_start = Format(Today), range_days = 7 });

        var missing = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{StaplesPath}/999999/add-to-list",
            rangeJson,
            csrfToken: null);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(missing)).GetProperty("code").GetString().Should().Be(ErrorCodes.NotFound);

        var id = await CreateStapleAsync(client, "M8 S Inactive Add");
        (await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{StaplesPath}/{id}",
            """{"is_active":false}""",
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var inactive = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{StaplesPath}/{id}/add-to-list",
            rangeJson,
            csrfToken: null);
        inactive.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await CountManualItemsAsync()).Should().Be(0, "an inactive staple is never added");
    }

    [Theory]
    [InlineData("""{"range_start":"2026-11-14","range_days":7}""", "range_start")]
    [InlineData("""{"range_start":"2026-11-15","range_days":0}""", "range_days")]
    [InlineData("""{"range_start":"2026-11-15","range_days":367}""", "range_days")]
    [InlineData("""{"range_days":7}""", "range_start")]
    public async Task Staple_add_to_list_validates_the_range(string json, string field)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var id = await CreateStapleAsync(client, "M8 S Range Validation");

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{StaplesPath}/{id}/add-to-list",
            json,
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("details").GetProperty("field_errors")
            .TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Staple_add_to_list_accepts_the_366_day_boundary()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var id = await CreateStapleAsync(client, "M8 S Boundary Days");

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{StaplesPath}/{id}/add-to-list",
            JsonSerializer.Serialize(new { range_start = Format(Today), range_days = 366 }),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(response)).GetProperty("range_days").GetInt32().Should().Be(366);
    }

    [Fact]
    public async Task Staple_add_to_list_records_the_acting_user()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var adder = await SeedUserAsync("adder", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(adder.Id, PermissionKeys.GroceriesEdit);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using var session = await SignInAsync(factory, "adder", MemberPassword);

        var created = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("M8 S Actor Staple"),
            session.CsrfToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var stapleId = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        var added = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{StaplesPath}/{stapleId}/add-to-list",
            JsonSerializer.Serialize(new { range_start = Format(Today), range_days = 7 }),
            session.CsrfToken);
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var manualItemId = (await ReadJsonAsync(added)).GetProperty("id").GetInt64();

        var stored = await GetManualItemAsync(manualItemId);
        stored.Should().NotBeNull();
        stored!.CreatedByUserId.Should().Be(adder.Id, "the manual item records the acting member");
    }

    // Check state

    [Fact]
    public async Task Check_then_uncheck_round_trip_and_idempotency()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var identity = NewIdentity();
        var body = JsonSerializer.Serialize(new { identity });

        var checkedResponse = await SendJsonAsync(client, HttpMethod.Post, CheckPath, body, csrfToken: null);
        checkedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var checkedBody = await ReadJsonAsync(checkedResponse);
        checkedBody.GetProperty("identity").GetString().Should().Be(identity);
        checkedBody.GetProperty("checked").GetBoolean().Should().BeTrue();
        checkedBody.GetProperty("checked_at").GetString().Should().NotBeNullOrEmpty();
        checkedBody.GetProperty("checked_by_user_id").ValueKind.Should().Be(JsonValueKind.Null);

        // Repeating the check is idempotent and never duplicates the unique row.
        (await SendJsonAsync(client, HttpMethod.Post, CheckPath, body, csrfToken: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountCheckStatesAsync(identity)).Should().Be(1);

        var uncheckedResponse = await SendJsonAsync(client, HttpMethod.Post, UncheckPath, body, csrfToken: null);
        uncheckedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var uncheckedBody = await ReadJsonAsync(uncheckedResponse);
        uncheckedBody.GetProperty("checked").GetBoolean().Should().BeFalse();
        uncheckedBody.GetProperty("checked_at").ValueKind.Should().Be(JsonValueKind.Null);
        (await CountCheckStatesAsync(identity)).Should().Be(1);

        var stored = await GetCheckStateAsync(identity);
        stored.Should().NotBeNull();
        stored!.Checked.Should().BeFalse();
        stored.CheckedAt.Should().BeNull();
    }

    [Fact]
    public async Task Check_concurrent_first_writes_leave_one_checked_row()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var identity = NewIdentity();
        var body = JsonSerializer.Serialize(new { identity });

        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => SendJsonAsync(client, HttpMethod.Post, CheckPath, body, csrfToken: null)));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        (await CountCheckStatesAsync(identity))
            .Should().Be(1, "the unique identity hash collapses concurrent first writes");
        var stored = await GetCheckStateAsync(identity);
        stored.Should().NotBeNull();
        stored!.Checked.Should().BeTrue();
        stored.CheckedByUserId.Should().BeNull("the single-user system actor has no user row");
    }

    [Fact]
    public async Task Uncheck_concurrent_first_writes_leave_one_unchecked_row()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();
        var identity = NewIdentity();
        var body = JsonSerializer.Serialize(new { identity });

        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => SendJsonAsync(client, HttpMethod.Post, UncheckPath, body, csrfToken: null)));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        (await CountCheckStatesAsync(identity))
            .Should().Be(1, "the unique identity hash collapses concurrent first writes");
        var stored = await GetCheckStateAsync(identity);
        stored.Should().NotBeNull();
        stored!.Checked.Should().BeFalse();
        stored.CheckedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    public async Task Check_rejects_a_malformed_identity(string identity)
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity }),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("identity", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Check_is_reflected_in_the_grocery_list()
    {
        var ingredient = await SeedIngredientAsync("M8 S List Check", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 S List Check Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var before = await GetRootAsync(client, GroceriesPath);
        var item = ItemsNamed(before, "M8 S List Check").Single();
        item.GetProperty("checked").GetBoolean().Should().BeFalse();
        var identity = item.GetProperty("identity").GetString()!;

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity }),
            csrfToken: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await GetRootAsync(client, GroceriesPath);
        ItemsNamed(after, "M8 S List Check").Single()
            .GetProperty("checked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Checked_state_survives_a_quantity_change_for_the_same_identity()
    {
        var ingredient = await SeedIngredientAsync("M8 S Quantity", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 S Quantity Recipe");
        var lineId = await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var before = await GetRootAsync(client, GroceriesPath);
        var identity = ItemsNamed(before, "M8 S Quantity").Single().GetProperty("identity").GetString()!;
        (await SendJsonAsync(
            client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity }),
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        await UpdateRecipeIngredientQuantityAsync(lineId, 5m);

        var after = await GetRootAsync(client, GroceriesPath);
        var line = ItemsNamed(after, "M8 S Quantity").Single();
        line.GetProperty("identity").GetString().Should().Be(identity);
        line.GetProperty("quantity").GetDecimal().Should().Be(5m);
        line.GetProperty("checked").GetBoolean().Should().BeTrue("identity is quantity-independent");
    }

    [Fact]
    public async Task Different_dimensions_do_not_collapse_into_one_checkbox()
    {
        var cup = await SeedUnitAsync("cup", "cup", UnitDimension.Volume, 236.5882365m);
        var ingredient = await SeedIngredientAsync("M8 S Dimension Onion", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 S Dimension Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 0.5m, unitId: cup.Id);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var before = await GetRootAsync(client, GroceriesPath);
        var lines = ItemsNamed(before, "M8 S Dimension Onion");
        lines.Should().HaveCount(2);
        var countLine = lines.Single(line => line.GetProperty("unit").ValueKind == JsonValueKind.Null);
        var volumeLine = lines.Single(line => line.GetProperty("unit").GetString() == "cup");
        countLine.GetProperty("identity").GetString()
            .Should().NotBe(volumeLine.GetProperty("identity").GetString());

        (await SendJsonAsync(
            client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity = countLine.GetProperty("identity").GetString() }),
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await GetRootAsync(client, GroceriesPath);
        ItemsNamed(after, "M8 S Dimension Onion")
            .Single(line => line.GetProperty("unit").ValueKind == JsonValueKind.Null)
            .GetProperty("checked").GetBoolean().Should().BeTrue();
        ItemsNamed(after, "M8 S Dimension Onion")
            .Single(line => line.GetProperty("unit").GetString() == "cup")
            .GetProperty("checked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Checked_items_sort_below_unchecked_within_a_department()
    {
        var bakery = await SeedDepartmentAsync("M8 S Sort Bakery", sortOrder: 1);
        var apple = await SeedIngredientAsync("M8 S Sort Apple", bakery.Id);
        var banana = await SeedIngredientAsync("M8 S Sort Banana", bakery.Id);
        var cherry = await SeedIngredientAsync("M8 S Sort Cherry", bakery.Id);
        var recipe = await SeedRecipeAsync("M8 S Sort Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, apple.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, banana.Id, quantity: 1m, unitId: null);
        await SeedRecipeIngredientAsync(recipe.Id, cherry.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id);
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        var before = await GetRootAsync(client, GroceriesPath);
        var appleIdentity = ItemsNamed(before, "M8 S Sort Apple").Single().GetProperty("identity").GetString()!;
        (await SendJsonAsync(
            client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity = appleIdentity }),
            csrfToken: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await GetRootAsync(client, GroceriesPath);
        var bakeryGroup = after.GetProperty("departments").EnumerateArray()
            .Single(department => department.GetProperty("name").GetString() == "M8 S Sort Bakery");
        var ordered = bakeryGroup.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("name").GetString())
            .ToArray();
        ordered.Should().Equal("M8 S Sort Banana", "M8 S Sort Cherry", "M8 S Sort Apple");
    }

    [Fact]
    public async Task Check_state_is_household_shared_across_users()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var first = await SeedUserAsync("first", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(first.Id, PermissionKeys.GroceriesEdit);
        var second = await SeedUserAsync("second", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(second.Id, PermissionKeys.GroceriesEdit);
        var ingredient = await SeedIngredientAsync("M8 S Shared Ingredient", departmentId: null);
        var recipe = await SeedRecipeAsync("M8 S Shared Recipe");
        await SeedRecipeIngredientAsync(recipe.Id, ingredient.Id, quantity: 1m, unitId: null);
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using var firstSession = await SignInAsync(factory, "first", MemberPassword);
        var before = await GetRootAsync(firstSession.Client, GroceriesPath);
        var identity = ItemsNamed(before, "M8 S Shared Ingredient").Single().GetProperty("identity").GetString()!;

        var checkedResponse = await SendJsonAsync(
            firstSession.Client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity }),
            firstSession.CsrfToken);
        checkedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(checkedResponse)).GetProperty("checked_by_user_id").GetInt64()
            .Should().Be(first.Id);

        using var secondSession = await SignInAsync(factory, "second", MemberPassword);
        var after = await GetRootAsync(secondSession.Client, GroceriesPath);
        ItemsNamed(after, "M8 S Shared Ingredient").Single()
            .GetProperty("checked").GetBoolean().Should().BeTrue("check state is household-shared");
    }

    // Permissions

    [Fact]
    public async Task Permission_matrix_gates_staples_and_check_state()
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

        var identityBody = JsonSerializer.Serialize(new { identity = NewIdentity() });
        var rangeBody = JsonSerializer.Serialize(new { range_start = Format(Today), range_days = 7 });

        using (var anonymous = factory.CreateClient())
        {
            (await anonymous.GetAsync(StaplesPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await SendJsonAsync(anonymous, HttpMethod.Post, StaplesPath, StapleJson("M8 S Perm Anon"), csrfToken: null))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await SendJsonAsync(anonymous, HttpMethod.Post, CheckPath, identityBody, csrfToken: null))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // The anon 401 path is uniform through the permission filter: the
            // remaining staple writes and uncheck are challenged before any body
            // parsing, all with 401 AUTH_REQUIRED.
            var anonPatch = await SendJsonAsync(
                anonymous,
                HttpMethod.Patch,
                $"{StaplesPath}/1",
                """{"name":"M8 S Perm Anon"}""",
                csrfToken: null);
            anonPatch.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadErrorAsync(anonPatch)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.AuthRequired);

            var anonAddToList = await SendJsonAsync(
                anonymous,
                HttpMethod.Post,
                $"{StaplesPath}/1/add-to-list",
                rangeBody,
                csrfToken: null);
            anonAddToList.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadErrorAsync(anonAddToList)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.AuthRequired);

            var anonUncheck = await SendJsonAsync(
                anonymous,
                HttpMethod.Post,
                UncheckPath,
                identityBody,
                csrfToken: null);
            anonUncheck.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ReadErrorAsync(anonUncheck)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.AuthRequired);
        }

        using (var viewerSession = await SignInAsync(factory, "viewer", MemberPassword))
        {
            (await viewerSession.Client.GetAsync(StaplesPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Post,
                StaplesPath,
                StapleJson("M8 S Perm Viewer"),
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Patch,
                $"{StaplesPath}/1",
                """{"name":"M8 S Perm Viewer 2"}""",
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Post,
                $"{StaplesPath}/1/add-to-list",
                rangeBody,
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Post,
                CheckPath,
                identityBody,
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await SendJsonAsync(
                viewerSession.Client,
                HttpMethod.Post,
                UncheckPath,
                identityBody,
                viewerSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var nobodySession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            (await nobodySession.Client.GetAsync(StaplesPath, Ct)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden);
        }

        using (var editorSession = await SignInAsync(factory, "editor", MemberPassword))
        {
            var created = await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Post,
                StaplesPath,
                StapleJson("M8 S Perm Editor"),
                editorSession.CsrfToken);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

            (await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Patch,
                $"{StaplesPath}/{id}",
                """{"name":"M8 S Perm Editor Renamed"}""",
                editorSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Post,
                $"{StaplesPath}/{id}/add-to-list",
                rangeBody,
                editorSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.Created);
            (await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Post,
                CheckPath,
                identityBody,
                editorSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SendJsonAsync(
                editorSession.Client,
                HttpMethod.Post,
                UncheckPath,
                identityBody,
                editorSession.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Single_user_system_actor_has_full_access_to_staples_and_check_state()
    {
        using var factory = await CreateSingleUserFactoryAsync(new TestTimeProvider(Now));
        using var client = factory.CreateClient();

        (await client.GetAsync(StaplesPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson("M8 S System Staple"),
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var check = await SendJsonAsync(
            client,
            HttpMethod.Post,
            CheckPath,
            JsonSerializer.Serialize(new { identity = NewIdentity() }),
            csrfToken: null);
        check.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(check)).GetProperty("checked_by_user_id").ValueKind
            .Should().Be(JsonValueKind.Null, "the system actor has no user row");
    }

    // Helpers

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

    private static string StapleJson(
        string name,
        long? ingredientId = null,
        long? departmentId = null,
        decimal? quantity = null,
        long? unitId = null)
    {
        var payload = new Dictionary<string, object?> { ["name"] = name };
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
            payload["default_quantity_numeric"] = amount;
        }

        if (unitId is { } unit)
        {
            payload["unit_id"] = unit;
        }

        return JsonSerializer.Serialize(payload);
    }

    private async Task<long> CreateStapleAsync(
        HttpClient client,
        string name,
        long? ingredientId = null,
        decimal? quantity = null,
        long? unitId = null)
    {
        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            StaplesPath,
            StapleJson(name, ingredientId, departmentId: null, quantity, unitId),
            csrfToken: null);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
    }

    private static async Task<List<JsonElement>> GetStaplesAsync(HttpClient client, string? query = null)
    {
        var response = await client.GetAsync($"{StaplesPath}{query}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        return root.GetProperty("staples").EnumerateArray().ToList();
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

    private static string NewIdentity() =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M8 Staple Household",
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

    private async Task<long> SeedRecipeIngredientAsync(long recipeId, long ingredientId, decimal? quantity, long? unitId)
    {
        await using var context = fixture.CreateContext();
        var line = new RecipeIngredient
        {
            RecipeId = recipeId,
            IngredientId = ingredientId,
            QuantityNumeric = quantity,
            UnitId = unitId,
            SortOrder = 0,
        };
        context.RecipeIngredients.Add(line);
        await context.SaveChangesAsync(Ct);
        return line.Id;
    }

    private async Task UpdateRecipeIngredientQuantityAsync(long lineId, decimal quantity)
    {
        await using var context = fixture.CreateContext();
        var line = await context.RecipeIngredients.SingleAsync(candidate => candidate.Id == lineId, Ct);
        line.QuantityNumeric = quantity;
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedEntryAsync(DateOnly date, CalendarEntryType entryType, long? recipeId)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = entryType,
            RecipeId = recipeId,
            IncludeGroceries = true,
            SnapshotRecipeName = null,
            SnapshotDisplayText = entryType.ToString(),
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<GroceryStaple?> GetStapleAsync(long id)
    {
        await using var context = fixture.CreateContext();
        return await context.GroceryStaples.AsNoTracking()
            .SingleOrDefaultAsync(staple => staple.Id == id, Ct);
    }

    private async Task<ManualGroceryItem?> GetManualItemAsync(long id)
    {
        await using var context = fixture.CreateContext();
        return await context.ManualGroceryItems.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, Ct);
    }

    private async Task<int> CountStaplesAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.GroceryStaples.CountAsync(Ct);
    }

    private async Task<int> CountManualItemsAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.ManualGroceryItems.CountAsync(Ct);
    }

    private async Task<GroceryCheckState?> GetCheckStateAsync(string identity)
    {
        await using var context = fixture.CreateContext();
        return await context.GroceryCheckStates.AsNoTracking()
            .SingleOrDefaultAsync(state => state.ItemIdentityHash == identity, Ct);
    }

    private async Task<int> CountCheckStatesAsync(string identity)
    {
        await using var context = fixture.CreateContext();
        return await context.GroceryCheckStates
            .CountAsync(state => state.ItemIdentityHash == identity, Ct);
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
