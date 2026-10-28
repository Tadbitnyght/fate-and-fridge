using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Route × role authorization battery. The suite covers evaluator semantics against
/// representative routes; this suite enumerates every registered
/// <c>/api/v1</c> product route and asserts the enforced
/// outcome for anonymous callers, a member without grants, a member holding
/// every key, an Admin, and the single-user system actor
/// <list type="bullet">
/// <item>anonymous → <c>401 AUTH_REQUIRED</c> (the explicitly listed public
/// routes keep their documented anonymous behavior);</item>
/// <item>a member missing the required key → <c>403 FORBIDDEN</c>, never 200 or
/// 500, including every Admin-only route;</item>
/// <item>an actor allowed by the gate → never 401/403/5xx (a malformed minimal
/// body may yield 400/404/409/422/415);</item>
/// <item>no route leaks a 5xx and every error body keeps the shared envelope
/// shape.</item>
/// </list>
/// The expected gate for each route is derived from docs/API.md and the
/// endpoint's documented contract, not from the endpoint metadata: the role
/// probes assert the enforced allow/deny outcome, and a third assertion compares
/// each route's enforced <c>PermissionRequirement</c> metadata (kind and
/// permission key) against the same oracle, so a route whose requirement
/// silently changed from one key to another fails even when every role probe
/// still sees the same 401/403/2xx. A second assertion checks that the
/// hand-written table exactly matches the routes the host actually registered
/// so a new route cannot escape the battery. The same inventory proves the
/// retired OpenAPI route and permission probes are absent. Requires a
/// Docker daemon; run with <c>dotnet test --filter "Category!=Integration"</c>
/// to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class RouteRoleMatrixIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string ProbeId = "999999";
    private const string OperationId = "11111111-1111-1111-1111-111111111111";

    // Requirement metadata for Admin-gated routes.
    private static readonly PermissionRequirement AdminRequirement =
        new(PermissionRequirementKind.Admin);

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date.
    private static readonly DateTimeOffset Now =
        new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly DateOnly FutureDate = Today.AddDays(5);

    private static readonly string TodayText = Today.ToString("yyyy-MM-dd");
    private static readonly string FutureDateText = FutureDate.ToString("yyyy-MM-dd");

    // The spec oracle
    // One row per registered route (docs/API.md). `Gate` is derived by hand
    // from the documented contract; `Path` is the concrete probe request
    // `HappyPathStatus` is set where a valid minimal request cheaply proves the
    // allowed actor actually reaches the handler.
    private static readonly IReadOnlyList<RouteCase> Routes =
    [
        // Settings / About / Updates
        new("GET", "/api/v1/about", "/api/v1/about", Gate.Authenticated, HappyPathStatus: 200),
        new("GET", "/api/v1/updates", "/api/v1/updates", Gate.Authenticated, HappyPathStatus: 200),
        new("GET", "/api/v1/settings/household", "/api/v1/settings/household", Gate.Authenticated, HappyPathStatus: 200),
        new("PATCH", "/api/v1/settings/household", "/api/v1/settings/household", Gate.Admin, Body: """{"household_name":"M14 matrix household"}""", HappyPathStatus: 200),
        new("POST", "/api/v1/settings/auth-mode", "/api/v1/settings/auth-mode", Gate.Admin, Body: "{}"),
        new("GET", "/api/v1/settings/security", "/api/v1/settings/security", Gate.Admin, HappyPathStatus: 200),
        new("PATCH", "/api/v1/settings/security", "/api/v1/settings/security", Gate.Admin, Body: "{}"),
        new("POST", "/api/v1/admin/updates/check", "/api/v1/admin/updates/check", Gate.Admin, HappyPathStatus: 200),
        new("PATCH", "/api/v1/admin/updates", "/api/v1/admin/updates", Gate.Admin, Body: "{}"),

        // Auth
        new("POST", "/api/v1/auth/login", "/api/v1/auth/login", Gate.Public(400, ErrorCodes.ValidationFailed), Body: "{}"),
        new("GET", "/api/v1/auth/session", "/api/v1/auth/session", Gate.Public(401, ErrorCodes.AuthRequired)),
        new("POST", "/api/v1/auth/change-password", "/api/v1/auth/change-password", Gate.Session, Body: "{}"),

        // Setup / health (public by contract)
        new("GET", "/api/v1/setup/status", "/api/v1/setup/status", Gate.Public(200, null)),
        new("POST", "/api/v1/setup/complete", "/api/v1/setup/complete", Gate.Public(409, ErrorCodes.SetupAlreadyCompleted), Body: "{}"),
        new("POST", "/api/v1/setup/restore", "/api/v1/setup/restore", Gate.Public(409, ErrorCodes.SetupAlreadyCompleted), Body: "{}"),
        new("GET", "/api/v1/health", "/api/v1/health", Gate.Public(200, null)),
        new("GET", "/api/v1/health/db", "/api/v1/health/db", Gate.Public(200, null)),

        // Calendar
        new("GET", "/api/v1/calendar", $"/api/v1/calendar?start={TodayText}&end={TodayText}", Gate.Permission(PermissionKeys.CalendarView), HappyPathStatus: 200),
        new("GET", "/api/v1/calendar/{date}", $"/api/v1/calendar/{TodayText}", Gate.Permission(PermissionKeys.CalendarView), HappyPathStatus: 200),
        new("GET", "/api/v1/calendar/{date}/repeat-check", $"/api/v1/calendar/{TodayText}/repeat-check?recipe_id={ProbeId}", Gate.Permission(PermissionKeys.CalendarView), HappyPathStatus: 200),
        new("PUT", "/api/v1/calendar/{date}", $"/api/v1/calendar/{FutureDateText}", Gate.Permission(PermissionKeys.CalendarEdit), Body: """{"entry_type":"eat_out"}"""),
        new("DELETE", "/api/v1/calendar/{date}", $"/api/v1/calendar/{FutureDateText}", Gate.Permission(PermissionKeys.CalendarEdit), HappyPathStatus: 204),
        new("POST", "/api/v1/calendar/{date}/lock", $"/api/v1/calendar/{FutureDateText}/lock", Gate.Permission(PermissionKeys.CalendarEdit)),
        new("POST", "/api/v1/calendar/{date}/unlock", $"/api/v1/calendar/{FutureDateText}/unlock", Gate.Permission(PermissionKeys.CalendarEdit)),
        new("POST", "/api/v1/calendar/{date}/reroll", $"/api/v1/calendar/{FutureDateText}/reroll", Gate.Permission(PermissionKeys.CalendarEdit)),
        new("POST", "/api/v1/calendar/{date}/correction", $"/api/v1/calendar/{TodayText}/correction", Gate.Admin, Body: "{}"),
        new("POST", "/api/v1/calendar/reroll/preview", "/api/v1/calendar/reroll/preview", Gate.Permission(PermissionKeys.CalendarEdit), Body: "{}"),
        new("POST", "/api/v1/calendar/reroll/apply", "/api/v1/calendar/reroll/apply", Gate.Permission(PermissionKeys.CalendarEdit), Body: "{}"),

        // Operations
        new("POST", "/api/v1/operations/{operation_id}/undo", $"/api/v1/operations/{OperationId}/undo", Gate.Permission(PermissionKeys.CalendarEdit)),
        new("POST", "/api/v1/batch-operations/{batch_operation_id}/undo", $"/api/v1/batch-operations/{OperationId}/undo", Gate.Permission(PermissionKeys.CalendarEdit)),

        // Reference data: departments, units, ingredients, proteins, tags
        new("GET", "/api/v1/departments", "/api/v1/departments", Gate.Authenticated, HappyPathStatus: 200),
        new("POST", "/api/v1/departments", "/api/v1/departments", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("PATCH", "/api/v1/departments/{id:long}", $"/api/v1/departments/{ProbeId}", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("POST", "/api/v1/departments/{id:long}/retire", $"/api/v1/departments/{ProbeId}/retire", Gate.Permission(PermissionKeys.ReferenceDataEdit)),
        new("DELETE", "/api/v1/departments/{id:long}", $"/api/v1/departments/{ProbeId}", Gate.Admin),
        new("PUT", "/api/v1/departments/order", "/api/v1/departments/order", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),

        new("GET", "/api/v1/units", "/api/v1/units", Gate.Authenticated, HappyPathStatus: 200),
        new("POST", "/api/v1/units", "/api/v1/units", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("PATCH", "/api/v1/units/{id:long}", $"/api/v1/units/{ProbeId}", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("POST", "/api/v1/units/{id:long}/aliases", $"/api/v1/units/{ProbeId}/aliases", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("DELETE", "/api/v1/units/{id:long}/aliases/{aliasId:long}", $"/api/v1/units/{ProbeId}/aliases/{ProbeId}", Gate.Permission(PermissionKeys.ReferenceDataEdit)),
        new("POST", "/api/v1/units/{id:long}/retire", $"/api/v1/units/{ProbeId}/retire", Gate.Permission(PermissionKeys.ReferenceDataEdit)),
        new("DELETE", "/api/v1/units/{id:long}", $"/api/v1/units/{ProbeId}", Gate.Admin),

        new("GET", "/api/v1/ingredients", "/api/v1/ingredients", Gate.Authenticated, HappyPathStatus: 200),
        new("POST", "/api/v1/ingredients", "/api/v1/ingredients", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("POST", "/api/v1/ingredients/merge", "/api/v1/ingredients/merge", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("PATCH", "/api/v1/ingredients/{id:long}", $"/api/v1/ingredients/{ProbeId}", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("POST", "/api/v1/ingredients/{id:long}/aliases", $"/api/v1/ingredients/{ProbeId}/aliases", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("DELETE", "/api/v1/ingredients/{id:long}/aliases/{aliasId:long}", $"/api/v1/ingredients/{ProbeId}/aliases/{ProbeId}", Gate.Permission(PermissionKeys.ReferenceDataEdit)),
        new("POST", "/api/v1/ingredients/{id:long}/retire", $"/api/v1/ingredients/{ProbeId}/retire", Gate.Permission(PermissionKeys.ReferenceDataEdit)),
        new("DELETE", "/api/v1/ingredients/{id:long}", $"/api/v1/ingredients/{ProbeId}", Gate.Admin),

        new("GET", "/api/v1/proteins", "/api/v1/proteins", Gate.Authenticated, HappyPathStatus: 200),
        new("POST", "/api/v1/proteins", "/api/v1/proteins", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("PATCH", "/api/v1/proteins/{id:long}", $"/api/v1/proteins/{ProbeId}", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("POST", "/api/v1/proteins/{id:long}/retire", $"/api/v1/proteins/{ProbeId}/retire", Gate.Permission(PermissionKeys.ReferenceDataEdit)),
        new("DELETE", "/api/v1/proteins/{id:long}", $"/api/v1/proteins/{ProbeId}", Gate.Admin),

        new("GET", "/api/v1/tags", "/api/v1/tags", Gate.Authenticated, HappyPathStatus: 200),
        new("POST", "/api/v1/tags", "/api/v1/tags", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("PATCH", "/api/v1/tags/{id:long}", $"/api/v1/tags/{ProbeId}", Gate.Permission(PermissionKeys.ReferenceDataEdit), Body: "{}"),
        new("DELETE", "/api/v1/tags/{id:long}", $"/api/v1/tags/{ProbeId}", Gate.Admin),

        // Recipes and images
        new("GET", "/api/v1/recipes", "/api/v1/recipes", Gate.Permission(PermissionKeys.RecipesView), HappyPathStatus: 200),
        new("POST", "/api/v1/recipes", "/api/v1/recipes", Gate.Permission(PermissionKeys.RecipesEdit), Body: "{}"),
        new("GET", "/api/v1/recipes/{id:long}", $"/api/v1/recipes/{ProbeId}", Gate.Permission(PermissionKeys.RecipesView)),
        new("PATCH", "/api/v1/recipes/{id:long}", $"/api/v1/recipes/{ProbeId}", Gate.Permission(PermissionKeys.RecipesEdit), Body: "{}"),
        new("DELETE", "/api/v1/recipes/{id:long}", $"/api/v1/recipes/{ProbeId}", Gate.Admin),
        new("PUT", "/api/v1/recipes/{id:long}/image", $"/api/v1/recipes/{ProbeId}/image", Gate.Permission(PermissionKeys.RecipesEdit), Body: "{}"),
        new("POST", "/api/v1/recipes/{id:long}/image/from-url", $"/api/v1/recipes/{ProbeId}/image/from-url", Gate.Permission(PermissionKeys.RecipesEdit), Body: "{}"),
        new("PATCH", "/api/v1/recipes/{id:long}/image", $"/api/v1/recipes/{ProbeId}/image", Gate.Permission(PermissionKeys.RecipesEdit), Body: "{}"),
        new("DELETE", "/api/v1/recipes/{id:long}/image", $"/api/v1/recipes/{ProbeId}/image", Gate.Permission(PermissionKeys.RecipesEdit)),
        new("GET", "/api/v1/recipes/{id:long}/image", $"/api/v1/recipes/{ProbeId}/image", Gate.Permission(PermissionKeys.RecipesView)),

        // Groceries
        new("GET", "/api/v1/groceries", "/api/v1/groceries", Gate.Permission(PermissionKeys.GroceriesView), HappyPathStatus: 200),
        new("POST", "/api/v1/groceries/manual", "/api/v1/groceries/manual", Gate.Permission(PermissionKeys.GroceriesEdit), Body: "{}"),
        new("PATCH", "/api/v1/groceries/manual/{id:long}", $"/api/v1/groceries/manual/{ProbeId}", Gate.Permission(PermissionKeys.GroceriesEdit), Body: "{}"),
        new("DELETE", "/api/v1/groceries/manual/{id:long}", $"/api/v1/groceries/manual/{ProbeId}", Gate.Permission(PermissionKeys.GroceriesEdit)),
        new("GET", "/api/v1/groceries/preferences", "/api/v1/groceries/preferences", Gate.Permission(PermissionKeys.GroceriesView), HappyPathStatus: 200),
        new("PATCH", "/api/v1/groceries/preferences", "/api/v1/groceries/preferences", Gate.Permission(PermissionKeys.GroceriesView), Body: "{}"),
        new("GET", "/api/v1/groceries/staples", "/api/v1/groceries/staples", Gate.Permission(PermissionKeys.GroceriesView), HappyPathStatus: 200),
        new("POST", "/api/v1/groceries/staples", "/api/v1/groceries/staples", Gate.Permission(PermissionKeys.GroceriesEdit), Body: "{}"),
        new("PATCH", "/api/v1/groceries/staples/{id:long}", $"/api/v1/groceries/staples/{ProbeId}", Gate.Permission(PermissionKeys.GroceriesEdit), Body: "{}"),
        new("POST", "/api/v1/groceries/staples/{id:long}/add-to-list", $"/api/v1/groceries/staples/{ProbeId}/add-to-list", Gate.Permission(PermissionKeys.GroceriesEdit)),
        new("POST", "/api/v1/groceries/check", "/api/v1/groceries/check", Gate.Permission(PermissionKeys.GroceriesEdit), Body: "{}"),
        new("POST", "/api/v1/groceries/uncheck", "/api/v1/groceries/uncheck", Gate.Permission(PermissionKeys.GroceriesEdit), Body: "{}"),

        // Statistics
        new("GET", "/api/v1/statistics/overview", "/api/v1/statistics/overview", Gate.Permission(PermissionKeys.RecipesView), HappyPathStatus: 200),
        new("GET", "/api/v1/statistics/recipes/{id:long}", $"/api/v1/statistics/recipes/{ProbeId}", Gate.Permission(PermissionKeys.RecipesView)),
        new("GET", "/api/v1/statistics/history", "/api/v1/statistics/history", Gate.Permission(PermissionKeys.CalendarView), HappyPathStatus: 200),

        // Import
        new("POST", "/api/v1/import/recipe-url", "/api/v1/import/recipe-url", Gate.Permission(PermissionKeys.RecipesEdit), Body: "{}"),
        new("POST", "/api/v1/import/check", "/api/v1/import/check", Gate.Permission(PermissionKeys.RecipesView), Body: "{}", HappyPathStatus: 200),

        // Users (Admin only)
        new("GET", "/api/v1/users", "/api/v1/users", Gate.Admin, HappyPathStatus: 200),
        new("POST", "/api/v1/users", "/api/v1/users", Gate.Admin, Body: "{}"),
        new("GET", "/api/v1/users/{id:long}", $"/api/v1/users/{ProbeId}", Gate.Admin),
        new("PATCH", "/api/v1/users/{id:long}", $"/api/v1/users/{ProbeId}", Gate.Admin, Body: "{}"),
        new("PATCH", "/api/v1/users/{id:long}/permissions", $"/api/v1/users/{ProbeId}/permissions", Gate.Admin, Body: "{}"),
        new("POST", "/api/v1/users/{id:long}/disable", $"/api/v1/users/{ProbeId}/disable", Gate.Admin),
        new("POST", "/api/v1/users/{id:long}/enable", $"/api/v1/users/{ProbeId}/enable", Gate.Admin),
        new("POST", "/api/v1/users/{id:long}/reset-password", $"/api/v1/users/{ProbeId}/reset-password", Gate.Admin),

        // Admin audit / backups / recovery key / update force
        new("GET", "/api/v1/admin/audit", "/api/v1/admin/audit", Gate.Admin, HappyPathStatus: 200),
        new("GET", "/api/v1/admin/audit/export", "/api/v1/admin/audit/export?format=csv", Gate.Admin, HappyPathStatus: 200),
        new("POST", "/api/v1/admin/audit/prune", "/api/v1/admin/audit/prune", Gate.Admin, Body: "{}"),
        new("GET", "/api/v1/admin/backups", "/api/v1/admin/backups", Gate.Admin, HappyPathStatus: 200),
        new("POST", "/api/v1/admin/backups", "/api/v1/admin/backups", Gate.Admin, Body: "{}"),
        new("GET", "/api/v1/admin/backups/{id:long}/download", $"/api/v1/admin/backups/{ProbeId}/download", Gate.Admin),
        new("DELETE", "/api/v1/admin/backups/{id:long}", $"/api/v1/admin/backups/{ProbeId}", Gate.Admin, Body: "{}"),
        new("POST", "/api/v1/admin/backups/restore", "/api/v1/admin/backups/restore", Gate.Admin),
        new("POST", "/api/v1/admin/recovery-key/export", "/api/v1/admin/recovery-key/export", Gate.Admin, Body: "{}"),

        // Last: a successful logout revokes the calling session, so no later
        // request may rely on it. Kept at the end of the table by design.
        new("POST", "/api/v1/auth/logout", "/api/v1/auth/logout", Gate.Session, HappyPathStatus: 204, RevokesSession: true),
    ];

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void The_expected_gate_table_covers_every_registered_api_route()
    {
        using var factory = fixture.CreateFactory();
        _ = factory.Services; // Builds the host and its endpoint data sources.

        var allRoutePatterns = factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        allRoutePatterns
            .Where(pattern => pattern.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("the OpenAPI document route must not be mapped");
        allRoutePatterns
            .Where(pattern => pattern.Contains("permission-probe", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("test-only permission probes must not ship");

        var registered = factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => Methods(endpoint).Select(method =>
                $"{method} {NormalizePattern(endpoint.RoutePattern.RawText!)}"))
            .Where(route => route.Split(' ')[1].StartsWith("/api/v1", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var expected = Routes
            .Select(route => $"{route.Method} {NormalizePattern(route.Pattern)}")
            .ToHashSet(StringComparer.Ordinal);

        registered.Should().BeEquivalentTo(
            expected,
            "every registered /api/v1 route must appear in the authz table (add new routes to it)");
    }

    [Fact]
    public async Task No_openapi_document_is_served()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json", cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType;

        var isJsonOpenApiDocument = response.StatusCode == HttpStatusCode.OK
            && mediaType is "application/json" or "application/vnd.oai.openapi+json"
            && raw.Contains("\"openapi\"", StringComparison.Ordinal)
            && raw.Contains("\"paths\"", StringComparison.Ordinal);

        isJsonOpenApiDocument.Should().BeFalse(
            "MapOpenApi is removed, so no OpenAPI JSON document may be served");
    }

    /// <summary>
    /// Every route must carry the access requirement its documented gate
    /// implies — the same <see cref="PermissionRequirement"/> metadata the
    /// enforcing filter reads — and a permission route must carry exactly the
    /// oracle's hand-written key. Without this, a route changed from
    /// <c>calendar.view</c> to <c>groceries.edit</c> still passes every role
    /// probe, because the probes only observe 401/403/2xx. The expectation comes
    /// from the table above (hand-written from docs/API.md), never from the
    /// metadata, so this is a comparison rather than a tautology.
    /// </summary>
    [Fact]
    public void Every_route_carries_the_permission_requirement_its_documented_gate_requires()
    {
        using var factory = fixture.CreateFactory();
        var endpoints = RegisteredEndpoints(factory);

        var compared = 0;
        foreach (var route in Routes)
        {
            var key = $"{route.Method} {NormalizePattern(route.Pattern)}";
            endpoints.Should().ContainKey(
                key,
                "every oracle row must match a registered route");
            var actual = endpoints[key].Metadata.GetOrderedMetadata<PermissionRequirement>();

            actual.Should().Equal(
                ExpectedRequirements(route),
                "the enforced requirement metadata of {0} must match its documented gate",
                key);
            compared++;
        }

        compared.Should().Be(Routes.Count);
        Routes.Should().HaveCount(105);
    }

    [Fact]
    public async Task Anonymous_requests_are_401_on_gated_routes_and_public_routes_keep_their_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var observations = new List<Observation>();
        foreach (var route in Routes)
        {
            var response = await SendAsync(client, route, csrfToken: null, cancellationToken);
            var observation = await ObserveAsync(route, "anonymous", response, cancellationToken);
            observations.Add(observation);

            if (route.Gate.Kind == GateKind.Public)
            {
                observation.Status.Should().Be(
                    (HttpStatusCode)route.Gate.PublicStatus!,
                    "{0} is public, route {1} {2}",
                    route.Path,
                    route.Method,
                    route.Pattern);
                if (route.Gate.PublicCode is { } code)
                {
                    observation.Code.Should().Be(code);
                }
            }
            else
            {
                observation.Status.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "anonymous callers must get 401 on {0} {1}",
                    route.Method,
                    route.Pattern);
                observation.Code.Should().Be(ErrorCodes.AuthRequired);
            }

            response.Dispose();
        }

        AssertNoServerErrors(observations);
        AssertErrorEnvelopes(observations);
    }

    [Fact]
    public async Task Member_without_grants_is_forbidden_on_every_key_and_admin_route()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "no-grants", Password, cancellationToken);

        var observations = await ProbeRoleAsync(
            session.Client,
            session.CsrfToken,
            "member-without-grants",
            allowsPermissionRoutes: false,
            allowsAdminRoutes: false,
            cancellationToken);

        AssertNoServerErrors(observations);
        AssertErrorEnvelopes(observations);
        AssertHappyPaths(observations, allowPermissionRoutes: false, allowAdminRoutes: false);
    }

    [Fact]
    public async Task Member_with_every_key_passes_permission_routes_and_is_forbidden_on_admin_routes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "all-keys", Password, cancellationToken);

        var observations = await ProbeRoleAsync(
            session.Client,
            session.CsrfToken,
            "member-all-keys",
            allowsPermissionRoutes: true,
            allowsAdminRoutes: false,
            cancellationToken);

        AssertNoServerErrors(observations);
        AssertErrorEnvelopes(observations);
        AssertHappyPaths(observations, allowPermissionRoutes: true, allowAdminRoutes: false);
    }

    [Fact]
    public async Task Admin_passes_every_gate_and_no_route_leaks_a_server_error()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", Password, cancellationToken);

        // docs/DATABASE.md §2: exactly seven keys, no eighth.
        PermissionKeys.All.Should().Equal(PermissionMatrix.AllKeys);
        PermissionKeys.All.Should().HaveCount(7);

        // Session payload agrees with the oracle for an Admin. Read it before
        // the matrix (which ends by logging out).
        var sessionResponse = await session.Client.GetAsync("/api/v1/auth/session", cancellationToken);
        var permissions = (await ReadJsonAsync(sessionResponse, cancellationToken))
            .GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .ToArray();
        permissions.Should().Equal(PermissionMatrix.AllKeys);

        var observations = await ProbeRoleAsync(
            session.Client,
            session.CsrfToken,
            "admin",
            allowsPermissionRoutes: true,
            allowsAdminRoutes: true,
            cancellationToken);

        AssertNoServerErrors(observations);
        AssertErrorEnvelopes(observations);
        AssertHappyPaths(observations, allowPermissionRoutes: true, allowAdminRoutes: true);

        // Every permission requirement on every registered route uses one of
        // the seven documented keys.
        var requirementKeys = factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<Endpoints.PermissionRequirement>())
            .Where(requirement => requirement.Kind == Endpoints.PermissionRequirementKind.Permission)
            .Select(requirement => requirement.PermissionKey)
            .ToArray();
        requirementKeys.Should().NotBeEmpty();
        requirementKeys.Should().OnlyContain(key => PermissionKeys.IsKnown(key));
    }

    [Fact]
    public async Task Single_user_system_actor_passes_every_gated_route_except_session_scoped_routes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(cancellationToken);

        using var factory = CreateFactory();
        using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE household_settings SET auth_mode = 'single_user'",
                cancellationToken);
        }

        using var client = factory.CreateClient();
        var observations = new List<Observation>();
        foreach (var route in Routes)
        {
            var response = await SendAsync(client, route, csrfToken: null, cancellationToken);
            var observation = await ObserveAsync(route, "system", response, cancellationToken);
            observations.Add(observation);

            if (route.Gate.Kind == GateKind.Public)
            {
                // Public routes keep serving in single-user mode; /auth/session
                // reports the system identity with 200. Only a 5xx is a defect
                // (asserted for the whole matrix below).
                response.Dispose();
                continue;
            }

            if (route.Gate.Kind == GateKind.Session)
            {
                // Single-user mode has no session and no user password: logout
                // and change-password are unreachable by design (no cookie, no
                // principal). They must still fail closed with the shared 401
                // envelope, never 403/500.
                response.StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized,
                    "session-scoped route {0} {1} is unreachable in single-user mode",
                    route.Method,
                    route.Pattern);
                observation.Code.Should().Be(ErrorCodes.AuthRequired);
                response.Dispose();
                continue;
            }

            response.StatusCode.Should().NotBe(
                HttpStatusCode.Unauthorized,
                "the single-user system actor holds every permission ({0} {1})",
                route.Method,
                route.Pattern);
            response.StatusCode.Should().NotBe(
                HttpStatusCode.Forbidden,
                "the single-user system actor is Admin and holds every key ({0} {1})",
                route.Method,
                route.Pattern);
            response.Dispose();
        }

        AssertNoServerErrors(observations);
        AssertErrorEnvelopes(observations);
    }

    /// <summary>
    /// Probes every route with an authenticated client, asserting the allowed
    /// outcome for each gate kind.
    /// </summary>
    private static async Task<List<Observation>> ProbeRoleAsync(
        HttpClient client,
        string? csrfToken,
        string role,
        bool allowsPermissionRoutes,
        bool allowsAdminRoutes,
        CancellationToken cancellationToken)
    {
        var observations = new List<Observation>();

        // `/auth/logout` is last in the table: it revokes the session.
        foreach (var route in Routes)
        {
            var response = await SendAsync(client, route, csrfToken, cancellationToken);
            var observation = await ObserveAsync(route, role, response, cancellationToken);
            observations.Add(observation);

            var allowed = route.Gate.Kind switch
            {
                GateKind.Authenticated or GateKind.Session => true,
                GateKind.Permission => allowsPermissionRoutes,
                GateKind.Admin => allowsAdminRoutes,
                GateKind.Public => true,
                _ => throw new InvalidOperationException($"Unexpected gate {route.Gate.Kind}."),
            };

            // Public routes have role-dependent bodies (for example
            // GET /auth/session is 200 for a session and 401 without one); the
            // anonymous test pins their documented anonymous behavior.
            if (route.Gate.Kind is GateKind.Authenticated or GateKind.Session
                or GateKind.Permission or GateKind.Admin)
            {
                if (allowed)
                {
                    observation.Status.Should().NotBe(
                        HttpStatusCode.Unauthorized,
                        "{0} may call {1} {2}",
                        role,
                        route.Method,
                        route.Pattern);
                    observation.Status.Should().NotBe(
                        HttpStatusCode.Forbidden,
                        "{0} may call {1} {2}",
                        role,
                        route.Method,
                        route.Pattern);
                }
                else
                {
                    observation.Status.Should().Be(
                        HttpStatusCode.Forbidden,
                        "{0} lacks the requirement for {1} {2}",
                        role,
                        route.Method,
                        route.Pattern);
                    observation.Code.Should().Be(ErrorCodes.Forbidden);
                }
            }

            response.Dispose();
        }

        return observations;
    }

    /// <summary>
    /// Asserts the documented happy path where a valid minimal request proves
    /// the allowed actor reaches the handler (2xx rather than only "not 401/403").
    /// </summary>
    private static void AssertHappyPaths(
        IEnumerable<Observation> observations,
        bool allowPermissionRoutes,
        bool allowAdminRoutes)
    {
        foreach (var observation in observations)
        {
            if (observation.Route.HappyPathStatus is not { } expected)
            {
                continue;
            }

            var allowed = observation.Route.Gate.Kind switch
            {
                GateKind.Authenticated or GateKind.Session or GateKind.Public => true,
                GateKind.Permission => allowPermissionRoutes,
                GateKind.Admin => allowAdminRoutes,
                _ => false,
            };

            if (!allowed)
            {
                continue;
            }

            ((int)observation.Status).Should().Be(
                expected,
                "the documented happy path for {0} {1} ({2})",
                observation.Route.Method,
                observation.Route.Pattern,
                observation.Role);
        }
    }

    private static void AssertNoServerErrors(IEnumerable<Observation> observations)
    {
        var failures = observations
            .Where(observation => (int)observation.Status >= 500)
            .Select(observation => $"{observation.Role} {observation.Route.Method} " +
                $"{observation.Route.Pattern} -> {(int)observation.Status}")
            .ToArray();

        failures.Should().BeEmpty("no route may leak a 5xx during the role matrix");
    }

    private static void AssertErrorEnvelopes(IEnumerable<Observation> observations)
    {
        foreach (var observation in observations.Where(item => (int)item.Status >= 400))
        {
            observation.EnvelopeValid.Should().BeTrue(
                "{0} {1} returned {2} without the shared error envelope (code/message/details)",
                observation.Route.Method,
                observation.Route.Pattern,
                (int)observation.Status);
        }
    }

    private static async Task<Observation> ObserveAsync(
        RouteCase route,
        string role,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? code = null;
        var envelopeValid = true;
        if ((int)response.StatusCode >= 400)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("error", out var error)
                    || error.ValueKind != JsonValueKind.Object
                    || !error.TryGetProperty("code", out var codeElement)
                    || codeElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(codeElement.GetString())
                    || !error.TryGetProperty("message", out var messageElement)
                    || messageElement.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(messageElement.GetString())
                    || !error.TryGetProperty("details", out var details)
                    || details.ValueKind != JsonValueKind.Object)
                {
                    envelopeValid = false;
                }
                else
                {
                    code = codeElement.GetString();
                }
            }
            catch (JsonException)
            {
                envelopeValid = false;
            }
        }

        return new Observation(route, role, response.StatusCode, code, envelopeValid);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        RouteCase route,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
        if (route.Body is not null)
        {
            request.Content = new StringContent(route.Body, Encoding.UTF8, "application/json");
        }

        if (csrfToken is not null)
        {
            request.Headers.TryAddWithoutValidation(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static string NormalizePattern(string pattern) =>
        pattern.Length > 1 ? pattern.TrimEnd('/') : pattern;

    private static IEnumerable<string> Methods(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];

    /// <summary>
    /// The registered <c>/api/v1</c> routes keyed by <c>"METHOD pattern"</c>, so
    /// the requirement comparison can look up the endpoint behind each oracle
    /// row without re-deriving any expectation from it.
    /// </summary>
    private static IReadOnlyDictionary<string, RouteEndpoint> RegisteredEndpoints(
        WebApplicationFactory<Program> factory) =>
        factory.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => Methods(endpoint).Select(method =>
                (Route: RouteKey(method, endpoint), Endpoint: endpoint)))
            .Where(item => item.Route.Split(' ')[1].StartsWith("/api/v1", StringComparison.Ordinal))
            .ToDictionary(item => item.Route, item => item.Endpoint, StringComparer.Ordinal);

    private static string RouteKey(string method, RouteEndpoint endpoint) =>
        $"{method} {NormalizePattern(endpoint.RoutePattern.RawText!)}";

    /// <summary>
    /// The requirement metadata the documented gate implies: none for the public
    /// and session-scoped routes (their guards are not permission metadata), and
    /// the single requirement for authenticated/Admin/permission rows.
    /// </summary>
    private static IReadOnlyList<PermissionRequirement> ExpectedRequirements(RouteCase route) =>
        route.Gate.Kind switch
        {
            GateKind.Public or GateKind.Session => [],
            GateKind.Authenticated => [new PermissionRequirement(PermissionRequirementKind.Authenticated)],
            GateKind.Admin => [AdminRequirement],
            GateKind.Permission => [
                new PermissionRequirement(PermissionRequirementKind.Permission, route.Gate.Key)],
            _ => throw new InvalidOperationException(
                $"Gate {route.Gate.Kind} has no requirement comparison."),
        };

    private WebApplicationFactory<Program> CreateFactory() =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(new TestTimeProvider(Now));
        });

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M14 matrix household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.Multiuser,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(cancellationToken);

        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);

        // The forced update check must not leave the test host for the public
        // network; the preference is the documented off switch.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupCompletionService.UpdateCheckEnabledKey}, 'false'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'false'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);

        await SeedUserAsync(context, "root", UserRole.Admin, cancellationToken);
        var allKeys = await SeedUserAsync(context, "all-keys", UserRole.Member, cancellationToken);
        await SeedUserAsync(context, "no-grants", UserRole.Member, cancellationToken);

        foreach (var key in PermissionKeys.All)
        {
            context.UserPermissions.Add(new UserPermission
            {
                UserId = allKeys.Id,
                PermissionKey = key,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static async Task<User> SeedUserAsync(
        Data.AppDbContext context,
        string username,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(Password),
            Role = role,
            IsActive = true,
            MustChangePassword = false,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
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

        var response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var values)
            ? values.Single()
            : throw new InvalidOperationException("Login did not return an antiforgery token.");

        return new SignedInClient(client, token);
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.Clone();
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("error").GetProperty("code").GetString()!;

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    private sealed record Observation(
        RouteCase Route,
        string Role,
        HttpStatusCode Status,
        string? Code,
        bool EnvelopeValid);

    private sealed record RouteCase(
        string Method,
        string Pattern,
        string Path,
        Gate Gate,
        string? Body = null,
        int? HappyPathStatus = null,
        bool RevokesSession = false);

    private enum GateKind
    {
        Public,
        Authenticated,
        Session,
        Admin,
        Permission,
    }

    private sealed record Gate(GateKind Kind, string? Key = null, int? PublicStatus = null, string? PublicCode = null)
    {
        public static Gate Public(int status, string? code) =>
            new(GateKind.Public, PublicStatus: status, PublicCode: code);

        public static Gate Permission(string key) => new(GateKind.Permission, Key: key);

        public static Gate Authenticated { get; } = new(GateKind.Authenticated);

        public static Gate Session { get; } = new(GateKind.Session);

        public static Gate Admin { get; } = new(GateKind.Admin);
    }
}

