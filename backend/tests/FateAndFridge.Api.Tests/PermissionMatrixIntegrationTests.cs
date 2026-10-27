using System.Net;
using System.Text;
using System.Text.Json;
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
/// Server-side permission enforcement against representative product routes
/// (M1-09, docs/API.md §3): the enforced result for every key is compared
/// against the spec oracle in <see cref="PermissionMatrix"/>. Covers Admin, a
/// member with and without each key, unauthenticated requests, the single-user
/// system actor, immediate effect of grant changes, and forced-change gate
/// precedence. The exhaustive per-route battery lives in
/// <see cref="M14RouteRoleMatrixIntegrationTests"/>; this suite keeps the
/// evaluator semantics and a representative real route per permission key.
/// Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class PermissionMatrixIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly DateOnly FutureDate = Today.AddDays(30);
    private static readonly string TodayText = Today.ToString("yyyy-MM-dd");
    private static readonly string FutureDateText = FutureDate.ToString("yyyy-MM-dd");

    // One representative real product route per permission key. The M14 route
    // matrix owns the exhaustive per-route coverage; these rows prove the
    // evaluator result is enforced by a real endpoint, not a probe.
    private static readonly IReadOnlyDictionary<string, RouteProbe> RepresentativeRoutes =
        new Dictionary<string, RouteProbe>(StringComparer.Ordinal)
        {
            [PermissionKeys.CalendarView] = new(
                "GET",
                $"/api/v1/calendar?start={TodayText}&end={TodayText}",
                AllowedStatus: 200),
            // A delete on a blank future date answers 404 *after* the permission
            // gate (there is no entry to remove), so the assertion only requires
            // that the request reached the handler rather than 401/403/5xx.
            [PermissionKeys.CalendarEdit] = new(
                "DELETE",
                $"/api/v1/calendar/{FutureDateText}"),
            [PermissionKeys.GroceriesView] = new(
                "GET",
                "/api/v1/groceries",
                AllowedStatus: 200),
            [PermissionKeys.GroceriesEdit] = new(
                "POST",
                "/api/v1/groceries/check",
                Body: "{}"),
            [PermissionKeys.RecipesView] = new(
                "GET",
                "/api/v1/recipes",
                AllowedStatus: 200),
            [PermissionKeys.RecipesEdit] = new(
                "POST",
                "/api/v1/recipes",
                Body: "{}"),
            [PermissionKeys.ReferenceDataEdit] = new(
                "POST",
                "/api/v1/departments",
                Body: "{}"),
        };

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Admin_passes_an_admin_only_route_and_a_permission_route()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", Password, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", Password, cancellationToken);

        // Admin-only requirement.
        (await session.Client.GetAsync("/api/v1/users", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Permission requirement (recipes.view), held by every Admin.
        (await session.Client.GetAsync("/api/v1/recipes", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Member_with_a_key_is_allowed_and_without_it_denied_on_real_routes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var granted = await SeedUserAsync("jane", Password, cancellationToken, UserRole.Member);
        await SeedUserAsync("bob", Password, cancellationToken, UserRole.Member);
        foreach (var key in PermissionKeys.All)
        {
            await SeedPermissionAsync(granted.Id, key, cancellationToken);
        }

        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var grantedSession = await SignInAsync(factory, "jane", Password, cancellationToken);
        using var deniedSession = await SignInAsync(factory, "bob", Password, cancellationToken);

        RepresentativeRoutes.Keys.Should().Equal(
            PermissionMatrix.AllKeys,
            "the representative table must cover every documented permission key exactly once");

        foreach (var (key, route) in RepresentativeRoutes)
        {
            using var allowed = await SendAsync(
                grantedSession.Client,
                grantedSession.CsrfToken,
                route,
                cancellationToken);
            AssertAllowed(allowed, route, key);

            using var denied = await SendAsync(
                deniedSession.Client,
                deniedSession.CsrfToken,
                route,
                cancellationToken);
            denied.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                "a member without {0} must be denied on {1} {2}",
                key,
                route.Method,
                route.Path);
            (await ReadErrorCodeAsync(denied, cancellationToken)).Should().Be(ErrorCodes.Forbidden);
        }

        // GET /auth/session reports the same evaluator output.
        var grantedSessionResponse = await grantedSession.Client.GetAsync(
            "/api/v1/auth/session",
            cancellationToken);
        var grantedRoot = await ReadJsonAsync(grantedSessionResponse, cancellationToken);
        grantedRoot.GetProperty("permissions").EnumerateArray()
            .Select(element => element.GetString()!)
            .Should().Equal(PermissionMatrix.AllKeys);

        var deniedSessionResponse = await deniedSession.Client.GetAsync(
            "/api/v1/auth/session",
            cancellationToken);
        var deniedRoot = await ReadJsonAsync(deniedSessionResponse, cancellationToken);
        deniedRoot.GetProperty("permissions").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Single_user_system_actor_passes_admin_and_permission_routes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/users", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/recipes", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unauthenticated_real_route_returns_401_envelope_with_no_store()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/recipes", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.AuthRequired);
        error.GetProperty("details").EnumerateObject().Should().BeEmpty();
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Permission_changes_take_effect_on_the_next_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var member = await SeedUserAsync("jane", Password, cancellationToken, UserRole.Member);
        await SeedUserAsync("root", Password, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var memberSession = await SignInAsync(factory, "jane", Password, cancellationToken);
        using var adminSession = await SignInAsync(factory, "root", Password, cancellationToken);

        (await memberSession.Client.GetAsync("/api/v1/recipes", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        // Grant mid-test through the product's Admin-only permissions route:
        // the next request must observe it.
        (await PatchPermissionsAsync(
            adminSession,
            member.Id,
            [PermissionKeys.RecipesView],
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await memberSession.Client.GetAsync("/api/v1/recipes", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Edit implies view, and the product route observes it immediately too.
        (await PatchPermissionsAsync(
            adminSession,
            member.Id,
            [PermissionKeys.CalendarEdit],
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await memberSession.Client.GetAsync(
            $"/api/v1/calendar?start={TodayText}&end={TodayText}",
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Revoke mid-test: the next request must observe that too.
        (await PatchPermissionsAsync(
            adminSession,
            member.Id,
            [],
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await memberSession.Client.GetAsync("/api/v1/recipes", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await memberSession.Client.GetAsync(
            $"/api/v1/calendar?start={TodayText}&end={TodayText}",
            cancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var sessionResponse = await memberSession.Client.GetAsync(
            "/api/v1/auth/session",
            cancellationToken);
        var sessionRoot = await ReadJsonAsync(sessionResponse, cancellationToken);
        sessionRoot.GetProperty("permissions").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Forced_change_gate_wins_over_permission_enforcement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", Password, cancellationToken, UserRole.Admin, mustChangePassword: true);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "root", Password, cancellationToken);

        // Even an actor who holds every key is blocked while flagged.
        var permissionRoute = await session.Client.GetAsync("/api/v1/recipes", cancellationToken);
        permissionRoute.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(permissionRoute, cancellationToken)).Should()
            .Be(ErrorCodes.PasswordChangeRequired);

        var adminRoute = await session.Client.GetAsync("/api/v1/users", cancellationToken);
        adminRoute.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(adminRoute, cancellationToken)).Should()
            .Be(ErrorCodes.PasswordChangeRequired);
    }

    private static async Task<HttpResponseMessage> PatchPermissionsAsync(
        SignedInClient session,
        long userId,
        IReadOnlyList<string> permissions,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/users/{userId}/permissions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { permissions }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(CsrfMiddleware.HeaderName, session.CsrfToken);

        return await session.Client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string csrfToken,
        RouteProbe route,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
        if (route.Body is not null)
        {
            request.Content = new StringContent(route.Body, Encoding.UTF8, "application/json");
        }

        request.Headers.TryAddWithoutValidation(CsrfMiddleware.HeaderName, csrfToken);
        return await client.SendAsync(request, cancellationToken);
    }

    private static void AssertAllowed(HttpResponseMessage response, RouteProbe route, string key)
    {
        if (route.AllowedStatus is { } expected)
        {
            ((int)response.StatusCode).Should().Be(
                expected,
                "a member holding {0} must reach the handler for {1} {2}",
                key,
                route.Method,
                route.Path);
            return;
        }

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "a member holding {0} may call {1} {2}",
            key,
            route.Method,
            route.Path);
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "a member holding {0} may call {1} {2}",
            key,
            route.Method,
            route.Path);
        ((int)response.StatusCode).Should().BeLessThan(500);
    }

    private sealed record RouteProbe(string Method, string Path, string? Body = null, int? AllowedStatus = null);

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

        var response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var values)
            ? values.Single()
            : throw new InvalidOperationException("Login did not return an antiforgery token.");

        return new SignedInClient(client, token);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Permission Matrix Household",
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
        UserRole role = UserRole.Member,
        bool mustChangePassword = false)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            MustChangePassword = mustChangePassword,
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

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("error").GetProperty("code").GetString()!;
}
