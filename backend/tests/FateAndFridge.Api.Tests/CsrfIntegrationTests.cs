using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// CSRF matrix for authenticated cookie requests (docs/API.md §4/§5,
/// docs/SECURITY.md §15): authenticated unsafe methods require the
/// <c>X-CSRF-TOKEN</c> header and the token is bound to the session, safe
/// methods are not validated, and requests without a valid session are never
/// CSRF-validated (they fail as <c>401 AUTH_REQUIRED</c>). Requires a Docker
/// daemon; run with <c>dotnet test --filter "Category!=Integration"</c> to skip
/// locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class CsrfIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string AntiforgeryCookieName = "faf_csrf";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly Uri LocalBaseAddress = new("http://localhost/");

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Unsafe_request_with_session_cookie_and_no_token_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);

        var response = await PatchAsync(
            session.Client,
            """{"household_name": "CSRF"}""",
            null,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, ErrorCodes.CsrfValidationFailed, cancellationToken);
        await AssertHouseholdNameAsync("Test Household", cancellationToken);
    }

    [Fact]
    public async Task Unsafe_request_with_an_invalid_token_is_rejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);

        var response = await PatchAsync(
            session.Client,
            """{"household_name": "CSRF"}""",
            "not-a-real-token",
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, ErrorCodes.CsrfValidationFailed, cancellationToken);
        await AssertHouseholdNameAsync("Test Household", cancellationToken);
    }

    /// <summary>
    /// M14-05: every cookie-authenticated unsafe route in the API must reject a
    /// missing or invalid <c>X-CSRF-TOKEN</c> with <c>403 CSRF_VALIDATION_FAILED</c>.
    /// The route list is read from the running host, so a newly added unsafe
    /// route is covered automatically. The two documented exemptions are the
    /// unauthenticated login route and the public setup-completion route.
    /// </summary>
    [Fact]
    public async Task Every_cookie_authenticated_unsafe_route_requires_a_valid_csrf_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);

        var unsafeRoutes = factory.Services
            .GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/v1", StringComparison.Ordinal))
            .SelectMany(endpoint => (endpoint.Metadata
                    .GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? [])
                .Where(method => method is not ("GET" or "HEAD" or "OPTIONS"))
                .Select(method => (Method: method, Pattern: endpoint.RoutePattern.RawText!)))
            .Where(route => route is not
                ("POST", "/api/v1/auth/login") and not
                ("POST", "/api/v1/setup/complete"))
            .Select(route => (route.Method, Path: ConcretePath(route.Pattern)))
            .ToArray();

        unsafeRoutes.Should().NotBeEmpty("the host must expose unsafe API routes");

        foreach (var (method, path) in unsafeRoutes)
        {
            var missing = await SendUnsafeAsync(session.Client, method, path, token: null, cancellationToken);
            try
            {
                missing.StatusCode.Should().Be(
                    HttpStatusCode.Forbidden,
                    "{0} {1} must reject a missing CSRF token",
                    method,
                    path);
                await AssertErrorCodeAsync(missing, ErrorCodes.CsrfValidationFailed, cancellationToken);
            }
            finally
            {
                missing.Dispose();
            }

            var invalid = await SendUnsafeAsync(
                session.Client,
                method,
                path,
                token: "not-a-real-token",
                cancellationToken);
            try
            {
                invalid.StatusCode.Should().Be(
                    HttpStatusCode.Forbidden,
                    "{0} {1} must reject an invalid CSRF token",
                    method,
                    path);
                await AssertErrorCodeAsync(invalid, ErrorCodes.CsrfValidationFailed, cancellationToken);
            }
            finally
            {
                invalid.Dispose();
            }
        }
    }

    [Fact]
    public async Task Unsafe_requests_are_csrf_exempt_in_single_user_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        // A mutating route, a destructive route, and a dangerous-action route:
        // none may be stopped by the antiforgery gate when no session exists.
        foreach (var (method, path) in new[]
        {
            ("PATCH", "/api/v1/settings/household"),
            ("DELETE", "/api/v1/calendar/2999-01-01"),
            ("POST", "/api/v1/admin/audit/prune"),
        })
        {
            var response = await SendUnsafeAsync(client, method, path, token: null, cancellationToken);
            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                body.Should().NotContain(
                    ErrorCodes.CsrfValidationFailed,
                    "single-user mode carries no session and is exempt ({0} {1})",
                    method,
                    path);
            }
        }
    }

    [Fact]
    public async Task Safe_get_is_not_validated_and_returns_a_fresh_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);

        var response = await session.Client.GetAsync("/api/v1/settings/household", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var values).Should().BeTrue();
        values!.Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Unsafe_request_with_a_valid_token_passes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);

        var csrfToken = await BootstrapCsrfTokenAsync(session.Client, cancellationToken);

        var response = await PatchAsync(
            session.Client,
            """{"household_name": "CSRF OK"}""",
            csrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertHouseholdNameAsync("CSRF OK", cancellationToken);
    }

    [Fact]
    public async Task Request_without_a_session_cookie_is_not_csrf_validated_in_single_user_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await PatchAsync(client, """{"household_name": "No CSRF"}""", null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertHouseholdNameAsync("No CSRF", cancellationToken);
    }

    [Fact]
    public async Task Request_without_a_session_cookie_is_auth_required_not_csrf_in_multiuser_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await PatchAsync(client, """{"household_name": "No CSRF"}""", null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCodeAsync(response, ErrorCodes.AuthRequired, cancellationToken);
    }

    [Fact]
    public async Task Token_minted_for_one_session_is_rejected_for_another_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var user = await SeedUserAsync("alice", cancellationToken);
        var sessionA = await CreateSessionClientAsync(factory, user, cancellationToken);
        var sessionB = await CreateSessionClientAsync(factory, user, cancellationToken);

        var tokenFromA = await BootstrapCsrfTokenAsync(sessionA.Client, cancellationToken);
        var antiforgeryCookieFromA = GetCookieValue(sessionA.Cookies, AntiforgeryCookieName);

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
        {
            Content = new StringContent(
                """{"household_name": "Bypass"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(
            "Cookie",
            $"{SessionAuthenticationDefaults.CookieName}={sessionB.RawToken}; " +
            $"{AntiforgeryCookieName}={antiforgeryCookieFromA}");
        request.Headers.Add(CsrfMiddleware.HeaderName, tokenFromA);

        var response = await factory.CreateDefaultClient().SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, ErrorCodes.CsrfValidationFailed, cancellationToken);
        await AssertHouseholdNameAsync("Test Household", cancellationToken);
    }

    [Fact]
    public async Task Token_minted_before_rotation_is_rejected_after_rotation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);

        var staleToken = await BootstrapCsrfTokenAsync(session.Client, cancellationToken);
        var staleAntiforgeryCookie = GetCookieValue(session.Cookies, AntiforgeryCookieName);

        string? rotatedToken;
        using (var scope = factory.Services.CreateScope())
        {
            rotatedToken = await scope.ServiceProvider
                .GetRequiredService<SessionService>()
                .RotateAsync(session.SessionId, cancellationToken);
        }

        rotatedToken.Should().NotBeNull();

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
        {
            Content = new StringContent(
                """{"household_name": "Rotated"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(
            "Cookie",
            $"{SessionAuthenticationDefaults.CookieName}={rotatedToken}; " +
            $"{AntiforgeryCookieName}={staleAntiforgeryCookie}");
        request.Headers.Add(CsrfMiddleware.HeaderName, staleToken);

        var response = await factory.CreateDefaultClient().SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, ErrorCodes.CsrfValidationFailed, cancellationToken);
        await AssertHouseholdNameAsync("Test Household", cancellationToken);
    }

    [Fact]
    public async Task Csrf_header_name_is_case_insensitive()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);
        var csrfToken = await BootstrapCsrfTokenAsync(session.Client, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
        {
            Content = new StringContent(
                """{"household_name": "Lower Case"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-csrf-token", csrfToken);

        var response = await session.Client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertHouseholdNameAsync("Lower Case", cancellationToken);
    }

    [Fact]
    public async Task Token_in_the_query_string_is_ignored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);
        var csrfToken = await BootstrapCsrfTokenAsync(session.Client, cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/settings/household?X-CSRF-TOKEN={Uri.EscapeDataString(csrfToken)}")
        {
            Content = new StringContent(
                """{"household_name": "Query"}""",
                Encoding.UTF8,
                "application/json"),
        };

        var response = await session.Client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(response, ErrorCodes.CsrfValidationFailed, cancellationToken);
        await AssertHouseholdNameAsync("Test Household", cancellationToken);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Safe_methods_are_not_csrf_validated(string method)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);

        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            "/api/v1/settings/household");
        var response = await session.Client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.MethodNotAllowed);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        body.Should().NotContain(ErrorCodes.CsrfValidationFailed);
    }

    [Fact]
    public async Task Antiforgery_cookie_without_a_session_cookie_is_not_csrf_validated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var session = await CreateAuthenticatedClientAsync(factory, "admin", cancellationToken);
        var csrfToken = await BootstrapCsrfTokenAsync(session.Client, cancellationToken);
        var antiforgeryCookie = GetCookieValue(session.Cookies, AntiforgeryCookieName);

        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
        {
            Content = new StringContent(
                """{"household_name": "No Session"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("Cookie", $"{AntiforgeryCookieName}={antiforgeryCookie}");
        request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);

        var response = await factory.CreateDefaultClient().SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCodeAsync(response, ErrorCodes.AuthRequired, cancellationToken);
    }

    [Fact]
    public async Task Invalid_session_cookie_with_unsafe_request_is_auth_required_and_cleared()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateDefaultClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
        {
            Content = new StringContent(
                """{"household_name": "Invalid"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add(
            "Cookie",
            $"{SessionAuthenticationDefaults.CookieName}=not-a-real-session");

        var response = await client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCodeAsync(response, ErrorCodes.AuthRequired, cancellationToken);

        response.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        setCookies!.Should().Contain(header => header.StartsWith(
            $"{SessionAuthenticationDefaults.CookieName}=",
            StringComparison.Ordinal));
    }

    private sealed record SessionClient(
        HttpClient Client,
        CookieContainer Cookies,
        long SessionId,
        string RawToken);

    private async Task<SessionClient> CreateAuthenticatedClientAsync(
        ApiFactory factory,
        string username,
        CancellationToken cancellationToken)
    {
        var user = await SeedUserAsync(username, cancellationToken);
        return await CreateSessionClientAsync(factory, user, cancellationToken);
    }

    private async Task<SessionClient> CreateSessionClientAsync(
        ApiFactory factory,
        User user,
        CancellationToken cancellationToken)
    {
        var cookies = new CookieContainer();
        var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));

        using var scope = factory.Services.CreateScope();
        var (session, rawToken) = await scope.ServiceProvider
            .GetRequiredService<SessionService>()
            .CreateAsync(user.Id, "127.0.0.1", "csrf-test", cancellationToken);

        cookies.Add(
            LocalBaseAddress,
            new Cookie(SessionAuthenticationDefaults.CookieName, rawToken) { Path = "/" });

        return new SessionClient(client, cookies, session.Id, rawToken);
    }

    private static async Task<string> BootstrapCsrfTokenAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var bootstrap = await client.GetAsync("/api/v1/settings/household", cancellationToken);
        bootstrap.StatusCode.Should().Be(HttpStatusCode.OK);
        return GetCsrfToken(bootstrap);
    }

    private async Task<User> SeedUserAsync(string username, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            PasswordHash = "phc-placeholder",
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Test Household",
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

    private async Task AssertHouseholdNameAsync(
        string expected,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var householdName = await context.HouseholdSettings
            .AsNoTracking()
            .Select(settings => settings.HouseholdName)
            .SingleAsync(cancellationToken);
        householdName.Should().Be(expected);
    }

    /// <summary>
    /// Turns a route pattern into a concrete path. Values are intentionally
    /// meaningless: the CSRF middleware rejects before any parameter binding,
    /// so only the route shape matters.
    /// </summary>
    private static string ConcretePath(string pattern) =>
        Regex.Replace(pattern, "\\{[^}]+\\}", "1");

    private static async Task<HttpResponseMessage> SendUnsafeAsync(
        HttpClient client,
        string method,
        string path,
        string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation(CsrfMiddleware.HeaderName, token);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        string json,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static string GetCsrfToken(HttpResponseMessage response)
    {
        response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var values).Should().BeTrue();
        var token = values!.Single();
        token.Should().NotBeNullOrWhiteSpace();
        return token;
    }

    private static string GetCookieValue(CookieContainer container, string name)
    {
        var cookie = container.GetCookies(LocalBaseAddress)[name];
        cookie.Should().NotBeNull();
        return cookie!.Value;
    }

    private static async Task AssertErrorCodeAsync(
        HttpResponseMessage response,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var error = payload.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(expectedCode);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        error.GetProperty("details").ValueKind.Should().Be(JsonValueKind.Object);
    }
}
