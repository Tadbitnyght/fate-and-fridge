using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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
using Geralt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the auth endpoints (docs/API.md §5
/// docs/SECURITY.md §2/§3): login/session/logout shapes, cookie flags
/// generic credential failures, validation, normalization, lock handling
/// rehash-on-login, counters, and auth-event hygiene. Lockout thresholds and
/// per-IP rate limits are part 2 and are not exercised here. Requires a Docker
/// daemon; run with <c>dotnet test --filter "Category!=Integration"</c> to skip
/// locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class AuthEndpointsIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string TestUserAgent = "auth-endpoints-test-agent";
    private const string TestRemoteIp = "203.0.113.7";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Login_with_valid_credentials_returns_the_session_payload_and_sets_a_persistent_cookie()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed("smith", Password, Role: UserRole.Admin),
            cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = WithRemoteIp(fixture.CreateFactory(), TestRemoteIp);
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", Password, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var root = await ReadJsonAsync(response, cancellationToken);
        root.EnumerateObject().Select(property => property.Name)
            .Should().Equal("authenticated", "auth_mode", "user", "permissions", "must_change_password");
        root.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        root.GetProperty("auth_mode").GetString().Should().Be("multiuser");
        root.GetProperty("must_change_password").GetBoolean().Should().BeFalse();

        var userElement = root.GetProperty("user");
        userElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("id", "username", "role");
        userElement.GetProperty("id").GetInt64().Should().Be(user.Id);
        userElement.GetProperty("username").GetString().Should().Be("smith");
        userElement.GetProperty("role").GetString().Should().Be("admin");
        root.GetProperty("permissions").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.All);

        var sessionCookie = GetSessionCookieHeader(response);
        sessionCookie.Should().StartWith($"{SessionAuthenticationDefaults.CookieName}=");
        HasCookieAttribute(sessionCookie, "httponly").Should().BeTrue();
        GetCookieAttributeValue(sessionCookie, "samesite").Should().Be("lax");
        GetCookieAttributeValue(sessionCookie, "path").Should().Be("/");
        HasCookieAttribute(sessionCookie, "secure").Should().BeFalse();
        var cookieExpires = GetCookieExpires(sessionCookie);
        cookieExpires.Should().NotBeNull();
        cookieExpires!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow.Add(SessionService.IdleTimeout),
            TimeSpan.FromMinutes(5));

        response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var csrfValues).Should().BeTrue();
        csrfValues!.Single().Should().NotBeNullOrWhiteSpace();

        await using var context = fixture.CreateContext();
        var session = await context.UserSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.UserId == user.Id, cancellationToken);
        session.RevokedAt.Should().BeNull();
        session.IpAddress.Should().Be(TestRemoteIp);
        session.UserAgent.Should().Be(TestUserAgent);
        session.IdleExpiresAt.Should().BeCloseTo(
            DateTimeOffset.UtcNow.Add(SessionService.IdleTimeout),
            TimeSpan.FromMinutes(5));
        session.AbsoluteExpiresAt.Should().BeCloseTo(
            DateTimeOffset.UtcNow.Add(SessionService.AbsoluteTimeout),
            TimeSpan.FromMinutes(5));

        var storedUser = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        storedUser.LastLoginAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        storedUser.FailedLoginCount.Should().Be(0);
        storedUser.LastFailedLoginAt.Should().BeNull();

        var authEvent = await context.AuthEvents.AsNoTracking().SingleAsync(cancellationToken);
        authEvent.EventType.Should().Be(AuthEventTypes.LoginSuccess);
        authEvent.UserId.Should().Be(user.Id);
        authEvent.UsernameAttempted.Should().Be("smith");
        authEvent.IpAddress.Should().Be(TestRemoteIp);
        authEvent.UserAgent.Should().Be(TestUserAgent);
        authEvent.MetadataJson.Should().BeNull();
    }

    [Theory]
    [InlineData("http", false)]
    [InlineData("https", true)]
    public async Task Login_cookie_is_secure_only_over_https(string scheme, bool expectSecure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync(new UserSeed("smith", Password), cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"{scheme}://localhost"),
            HandleCookies = false,
        });

        var response = await LoginAsync(client, "smith", Password, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        HasCookieAttribute(GetSessionCookieHeader(response), "secure").Should().Be(expectSecure);
    }

    [Fact]
    public async Task Credential_failures_return_identical_generic_401_bodies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync(new UserSeed("smith", Password), cancellationToken);
        await SeedUserAsync(new UserSeed("inactive", Password, IsActive: false), cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var wrongPassword = await LoginAsync(client, "smith", "wrong password entirely", cancellationToken);
        var unknownUser = await LoginAsync(client, "ghost", Password, cancellationToken);
        var inactiveUser = await LoginAsync(client, "inactive", Password, cancellationToken);

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownUser.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        inactiveUser.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var wrongBody = await wrongPassword.Content.ReadAsStringAsync(cancellationToken);
        var unknownBody = await unknownUser.Content.ReadAsStringAsync(cancellationToken);
        var inactiveBody = await inactiveUser.Content.ReadAsStringAsync(cancellationToken);

        wrongBody.Should().Contain(ErrorCodes.InvalidCredentials);
        unknownBody.Should().Be(wrongBody);
        inactiveBody.Should().Be(wrongBody);

        await using var context = fixture.CreateContext();
        (await context.UserSessions.CountAsync(cancellationToken)).Should().Be(0);
    }

    [Theory]
    [InlineData("""{"password":"correct horse battery staple"}""")]
    [InlineData("""{"username":"smith"}""")]
    [InlineData("""{"username":"   ","password":"correct horse battery staple"}""")]
    [InlineData("""{"username":"smith","password":""}""")]
    [InlineData("""{"username":123,"password":"correct horse battery staple"}""")]
    [InlineData("""{"username":"smith","password":42}""")]
    [InlineData("""{"username":null,"password":null}""")]
    [InlineData("""{"username":"smith","password":["x"]}""")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Login_validation_failures_return_400_validation_failed(string body)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, username: null, password: null, cancellationToken, rawBody: body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").EnumerateObject()
            .Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("  SMITH  ")]
    [InlineData("\tSmith\n")]
    public async Task Login_normalizes_username_case_and_whitespace(string attemptedUsername)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(new UserSeed("smith", Password), cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, attemptedUsername, Password, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var authEvent = await context.AuthEvents.AsNoTracking().SingleAsync(cancellationToken);
        authEvent.EventType.Should().Be(AuthEventTypes.LoginSuccess);
        authEvent.UserId.Should().Be(user.Id);
        authEvent.UsernameAttempted.Should().Be("smith");
    }

    [Fact]
    public async Task Login_for_a_locked_account_returns_423_with_locked_until()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var lockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed("smith", Password, LockedUntil: lockedUntil, FailedLoginCount: 9),
            cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", Password, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Locked);

        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.AccountLocked);
        var reportedLockedUntil = DateTimeOffset.Parse(
            error.GetProperty("details").GetProperty("locked_until").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        reportedLockedUntil.ToUniversalTime()
            .Should().BeCloseTo(lockedUntil.ToUniversalTime(), TimeSpan.FromSeconds(1));

        await using var context = fixture.CreateContext();
        (await context.UserSessions.CountAsync(cancellationToken)).Should().Be(0);
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(9);
        stored.LockedUntil.Should().BeCloseTo(lockedUntil, TimeSpan.FromMilliseconds(1));

        var authEvent = await context.AuthEvents.AsNoTracking().SingleAsync(cancellationToken);
        authEvent.EventType.Should().Be(AuthEventTypes.LoginFailure);
        authEvent.UserId.Should().Be(user.Id);
        authEvent.MetadataJson.Should().Contain(AuthService.AccountLockedReason);
    }

    [Fact]
    public async Task Login_proceeds_normally_after_the_lock_has_expired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed(
                "smith",
                Password,
                LockedUntil: DateTimeOffset.UtcNow.AddMinutes(-1),
                FailedLoginCount: 9),
            cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", Password, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        (await context.UserSessions.CountAsync(candidate => candidate.UserId == user.Id, cancellationToken))
            .Should().Be(1);
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(0);
        stored.LastFailedLoginAt.Should().BeNull();
    }

    [Fact]
    public async Task Login_rehashes_a_weak_stored_hash_after_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var hasher = new Argon2idPasswordHasher();
        var weakHash = CreateWeakHash(Password);
        hasher.NeedsRehash(weakHash).Should().BeTrue("the seeded hash uses a weaker memory parameter");

        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed("smith", Password, StoredHash: weakHash),
            cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", Password, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var storedHash = await context.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == user.Id)
            .Select(candidate => candidate.PasswordHash)
            .SingleAsync(cancellationToken);

        storedHash.Should().NotBe(weakHash);
        hasher.NeedsRehash(storedHash).Should().BeFalse();
        hasher.Verify(storedHash, Password).Should().BeTrue();
    }

    [Fact]
    public async Task Failed_login_increments_counters_and_stamps_last_failed_at()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed("smith", Password, FailedLoginCount: 3, LastFailedLoginAt: SeedTimestamp),
            cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", "wrong password entirely", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        await using var context = fixture.CreateContext();
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(4);
        stored.LastFailedLoginAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Successful_login_resets_failed_login_counters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed("smith", Password, FailedLoginCount: 4, LastFailedLoginAt: SeedTimestamp),
            cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", Password, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(0);
        stored.LastFailedLoginAt.Should().BeNull();
    }

    [Fact]
    public async Task Logout_revokes_the_session_clears_the_cookie_and_deauthenticates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(new UserSeed("smith", Password), cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var cookies = new CookieContainer();
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));

        var login = await LoginAsync(client, "smith", Password, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrfToken = login.Headers.GetValues(CsrfMiddleware.HeaderName).Single();

        long sessionId;
        await using (var context = fixture.CreateContext())
        {
            sessionId = await context.UserSessions
                .Where(candidate => candidate.UserId == user.Id)
                .Select(candidate => candidate.Id)
                .SingleAsync(cancellationToken);
        }

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        var logout = await client.SendAsync(logoutRequest, cancellationToken);

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        logout.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        var deletedCookie = setCookies!.Single(header => header.StartsWith(
            $"{SessionAuthenticationDefaults.CookieName}=",
            StringComparison.Ordinal));
        var deletedExpires = GetCookieExpires(deletedCookie);
        deletedExpires.Should().NotBeNull();
        deletedExpires!.Value.Should().BeBefore(DateTimeOffset.UtcNow);

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == sessionId, cancellationToken);
            stored.RevokedAt.Should().NotBeNull();
            stored.RevokedReason.Should().Be(AuthService.LogoutRevocationReason);
        }

        var sessionResponse = await client.GetAsync("/api/v1/auth/session", cancellationToken);
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(sessionResponse, cancellationToken)).Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Logout_revokes_only_the_current_session_of_a_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(new UserSeed("smith", Password), cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var (sessionAId, tokenA) = await CreateSessionAsync(factory, user, cancellationToken);
        var (sessionBId, tokenB) = await CreateSessionAsync(factory, user, cancellationToken);

        var cookies = new CookieContainer();
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        cookies.Add(
            new Uri("http://localhost/"),
            new Cookie(SessionAuthenticationDefaults.CookieName, tokenA) { Path = "/" });

        var bootstrap = await client.GetAsync("/api/v1/auth/session", cancellationToken);
        bootstrap.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrfToken = bootstrap.Headers.GetValues(CsrfMiddleware.HeaderName).Single();

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        var logout = await client.SendAsync(logoutRequest, cancellationToken);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        await using (var context = fixture.CreateContext())
        {
            var storedA = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == sessionAId, cancellationToken);
            storedA.RevokedAt.Should().NotBeNull();
            storedA.RevokedReason.Should().Be(AuthService.LogoutRevocationReason);

            var storedB = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == sessionBId, cancellationToken);
            storedB.RevokedAt.Should().BeNull();
        }

        using var otherClient = factory.CreateDefaultClient();
        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        sessionRequest.Headers.Add(
            "Cookie",
            $"{SessionAuthenticationDefaults.CookieName}={tokenB}");
        var sessionResponse = await otherClient.SendAsync(sessionRequest, cancellationToken);
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_without_a_session_returns_401_auth_required()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/v1/auth/logout", content: null, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Session_endpoint_returns_all_permissions_for_an_admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed("smith", Password, Role: UserRole.Admin),
            cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var (_, rawToken) = await CreateSessionAsync(factory, user, cancellationToken);
        using var client = factory.CreateDefaultClient();

        var response = await GetSessionAsync(client, rawToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.EnumerateObject().Select(property => property.Name)
            .Should().Equal("authenticated", "auth_mode", "user", "permissions", "must_change_password");
        root.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        root.GetProperty("auth_mode").GetString().Should().Be("multiuser");
        root.GetProperty("must_change_password").GetBoolean().Should().BeFalse();

        var userElement = root.GetProperty("user");
        userElement.GetProperty("id").GetInt64().Should().Be(user.Id);
        userElement.GetProperty("username").GetString().Should().Be("smith");
        userElement.GetProperty("role").GetString().Should().Be("admin");
        root.GetProperty("permissions").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.All);
    }

    [Fact]
    public async Task Session_endpoint_implies_view_permissions_for_a_member_with_must_change_password()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            new UserSeed("alice", Password, MustChangePassword: true),
            cancellationToken);
        await SeedPermissionAsync(user.Id, PermissionKeys.CalendarEdit, cancellationToken);
        await SeedPermissionAsync(user.Id, PermissionKeys.RecipesView, cancellationToken);
        await SeedPermissionAsync(user.Id, "bogus.key", cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var (_, rawToken) = await CreateSessionAsync(factory, user, cancellationToken);
        using var client = factory.CreateDefaultClient();

        var response = await GetSessionAsync(client, rawToken, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("must_change_password").GetBoolean().Should().BeTrue();
        root.GetProperty("user").GetProperty("role").GetString().Should().Be("member");
        root.GetProperty("permissions").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal(
                PermissionKeys.CalendarView,
                PermissionKeys.CalendarEdit,
                PermissionKeys.RecipesView);
    }

    [Fact]
    public async Task Session_endpoint_returns_401_auth_required_without_a_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/session", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.AuthRequired);
        error.GetProperty("details").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task Session_endpoint_reports_the_single_user_system_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/auth/session", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        root.GetProperty("auth_mode").GetString().Should().Be("single_user");
        root.GetProperty("user").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("must_change_password").GetBoolean().Should().BeFalse();
        root.GetProperty("permissions").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.All);
    }

    [Fact]
    public async Task Auth_events_record_attempt_context_without_password_hash_or_token_material()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string unknownPassword = "unknown-user-password-123456";
        const string wrongPassword = "wrong-password-123456789";

        var recorder = new RecordingLoggerProvider();
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync(new UserSeed("smith", Password), cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(recorder);
            });
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
                new RemoteIpStartupFilter(IPAddress.Parse(TestRemoteIp))));
        });
        using var client = factory.CreateClient();

        var unknown = await LoginAsync(client, "  Ghost  ", unknownPassword, cancellationToken);
        var wrong = await LoginAsync(client, "smith", wrongPassword, cancellationToken);
        var success = await LoginAsync(client, "smith", Password, cancellationToken);

        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        success.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawToken = ExtractSessionToken(GetSessionCookieHeader(success));

        await using var context = fixture.CreateContext();
        var storedHash = await context.Users
            .AsNoTracking()
            .Where(candidate => candidate.Username == "smith")
            .Select(candidate => candidate.PasswordHash)
            .SingleAsync(cancellationToken);

        var rows = await context.AuthEvents.AsNoTracking().ToListAsync(cancellationToken);
        rows.Should().HaveCount(3);

        var unknownRow = rows.Single(row => row.UserId is null);
        unknownRow.EventType.Should().Be(AuthEventTypes.LoginFailure);
        unknownRow.UsernameAttempted.Should().Be("ghost");
        unknownRow.IpAddress.Should().Be(TestRemoteIp);
        unknownRow.UserAgent.Should().Be(TestUserAgent);
        unknownRow.MetadataJson.Should().Contain(AuthService.UnknownUserReason);
        rows.Where(row => row.UserId is not null).Should().AllSatisfy(row =>
        {
            row.EventType.Should().BeOneOf(AuthEventTypes.LoginFailure, AuthEventTypes.LoginSuccess);
            row.IpAddress.Should().Be(TestRemoteIp);
            row.UserAgent.Should().Be(TestUserAgent);
        });

        var secrets = new[] { unknownPassword, wrongPassword, Password, storedHash, rawToken };
        foreach (var row in rows)
        {
            var rowText = string.Join(
                '\n',
                row.EventType,
                row.UsernameAttempted,
                row.MetadataJson,
                row.IpAddress,
                row.UserAgent);
            foreach (var secret in secrets)
            {
                rowText.Should().NotContain(
                    secret,
                    "auth event rows must never store password, hash, or token material");
            }
        }

        recorder.Messages.Should().NotBeEmpty();
        foreach (var secret in secrets)
        {
            recorder.AllText.Should().NotContain(
                text => text.Contains(secret, StringComparison.Ordinal),
                "logs must never contain password, hash, or token material");
        }
    }

    private sealed record UserSeed(
        string Username,
        string Password,
        UserRole Role = UserRole.Member,
        bool IsActive = true,
        bool MustChangePassword = false,
        DateTimeOffset? LockedUntil = null,
        int FailedLoginCount = 0,
        DateTimeOffset? LastFailedLoginAt = null,
        string? StoredHash = null);

    private static WebApplicationFactory<Program> WithRemoteIp(ApiFactory factory, string ipAddress) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(
                new RemoteIpStartupFilter(IPAddress.Parse(ipAddress)))));

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string? username,
        string? password,
        CancellationToken cancellationToken,
        string? rawBody = null,
        string? userAgent = TestUserAgent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                rawBody ?? JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };

        if (userAgent is not null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> GetSessionAsync(
        HttpClient client,
        string rawToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Add(
            "Cookie",
            $"{SessionAuthenticationDefaults.CookieName}={rawToken}");

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task<(long SessionId, string RawToken)> CreateSessionAsync(
        WebApplicationFactory<Program> factory,
        User user,
        CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var (session, rawToken) = await scope.ServiceProvider
            .GetRequiredService<SessionService>()
            .CreateAsync(user.Id, TestRemoteIp, TestUserAgent, cancellationToken);

        return (session.Id, rawToken);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Auth Test Household",
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

    private async Task<User> SeedUserAsync(UserSeed seed, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = seed.Username,
            NormalizedUsername = UsernameNormalizer.Normalize(seed.Username),
            PasswordHash = seed.StoredHash ?? new Argon2idPasswordHasher().Hash(seed.Password),
            Role = seed.Role,
            IsActive = seed.IsActive,
            MustChangePassword = seed.MustChangePassword,
            LockedUntil = seed.LockedUntil,
            FailedLoginCount = seed.FailedLoginCount,
            LastFailedLoginAt = seed.LastFailedLoginAt,
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

    private static string GetSessionCookieHeader(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var headers).Should().BeTrue();
        return headers!
            .Single(header => header.StartsWith(
                $"{SessionAuthenticationDefaults.CookieName}=",
                StringComparison.Ordinal));
    }

    private static bool HasCookieAttribute(string setCookieHeader, string attribute) =>
        setCookieHeader.Split(';')
            .Skip(1)
            .Select(segment => segment.Trim())
            .Any(segment => string.Equals(segment, attribute, StringComparison.OrdinalIgnoreCase));

    private static string? GetCookieAttributeValue(string setCookieHeader, string attribute) =>
        setCookieHeader.Split(';')
            .Skip(1)
            .Select(segment => segment.Trim())
            .Where(segment => segment.StartsWith($"{attribute}=", StringComparison.OrdinalIgnoreCase))
            .Select(segment => segment[(attribute.Length + 1)..])
            .FirstOrDefault();

    private static DateTimeOffset? GetCookieExpires(string setCookieHeader)
    {
        foreach (var segment in setCookieHeader.Split(';').Skip(1))
        {
            var trimmed = segment.Trim();
            if (!trimmed.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed["expires=".Length..];
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expires))
            {
                return expires;
            }
        }

        return null;
    }

    private static string ExtractSessionToken(string setCookieHeader)
    {
        var value = setCookieHeader[$"{SessionAuthenticationDefaults.CookieName}=".Length..];
        var terminator = value.IndexOf(';');
        return terminator < 0 ? value : value[..terminator];
    }

    /// <summary>
    /// Seeds a weaker-parameter Argon2id PHC hash (8 MiB instead of the
    /// production 64 MiB) so rehash-on-login can be observed end to end.
    /// </summary>
    private static string CreateWeakHash(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            Span<char> hashBuffer = stackalloc char[Argon2id.HashSize];
            Argon2id.ComputeHash(hashBuffer, passwordBytes, 2, 8 * 1024 * 1024);
            var terminator = hashBuffer.IndexOf('\0');
            return new string(hashBuffer[..terminator]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// TestServer leaves the client address unset; the event tests need a
    /// deterministic one, so this filter sets it before the app pipeline runs.
    /// </summary>
    private sealed class RemoteIpStartupFilter(IPAddress remoteIpAddress) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = remoteIpAddress;
                    return nextMiddleware(context);
                });

                next(application);
            };
    }
}
