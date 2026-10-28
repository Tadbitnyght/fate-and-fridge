using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the password lifecycle (docs/API.md §5
/// docs/SECURITY.md §2): change-password validation, session rotation, the
/// forced-change gate, other-session revocation, and event/log hygiene.
/// Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class PasswordLifecycleIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a much better passphrase";
    private const string TestUserAgent = "password-lifecycle-test";
    private const string AntiforgeryCookieName = "faf_csrf";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Change_password_rotates_the_session_clears_the_flag_and_revokes_other_sessions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);
        var (otherSessionId, otherToken) = await CreateSessionAsync(factory, user, cancellationToken);

        var oldHash = await GetStoredHashAsync(user.Id, cancellationToken);

        var response = await ChangePasswordAsync(
            session.Client,
            session.CsrfToken,
            Password,
            NewPassword,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        setCookies!.Should().Contain(header => header.StartsWith(
            $"{SessionAuthenticationDefaults.CookieName}=",
            StringComparison.Ordinal));
        setCookies!.Should().Contain(
            header => header.StartsWith($"{AntiforgeryCookieName}=", StringComparison.Ordinal),
            "the rotated session must receive a fresh antiforgery cookie");
        response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var csrfValues).Should().BeTrue();
        var rotatedCsrfToken = csrfValues!.Single();

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
            stored.MustChangePassword.Should().BeFalse();
            stored.PasswordHash.Should().NotBe(oldHash);
            new Argon2idPasswordHasher().Verify(stored.PasswordHash, NewPassword).Should().BeTrue();
            stored.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

            var currentSession = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == session.SessionId, cancellationToken);
            currentSession.RevokedAt.Should().BeNull();

            var otherSession = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == otherSessionId, cancellationToken);
            otherSession.RevokedAt.Should().NotBeNull();
            otherSession.RevokedReason.Should().Be(AuthService.PasswordChangeRevocationReason);

            var changeEvent = await context.AuthEvents
                .AsNoTracking()
                .SingleAsync(
                    authEvent => authEvent.EventType == AuthEventTypes.PasswordChange,
                    cancellationToken);
            changeEvent.UserId.Should().Be(user.Id);
            changeEvent.UsernameAttempted.Should().Be("smith");
            changeEvent.MetadataJson.Should().Contain("revoked_other_sessions");
        }

        // The pre-rotation token no longer resolves.
        using (var staleClient = factory.CreateDefaultClient())
        using (var staleRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session"))
        {
            staleRequest.Headers.Add(
                "Cookie",
                $"{SessionAuthenticationDefaults.CookieName}={session.RawToken}");
            (await staleClient.SendAsync(staleRequest, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        // The rotated cookie authenticates and the flag is gone.
        (await session.Client.GetAsync("/api/v1/auth/session", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync("/api/v1/settings/household", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // The rotated session received a fresh CSRF token in the change response.
        using (var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout"))
        {
            logoutRequest.Headers.Add(CsrfMiddleware.HeaderName, rotatedCsrfToken);
            (await session.Client.SendAsync(logoutRequest, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.NoContent);
        }

        // The other session's token is revoked.
        using (var otherClient = factory.CreateDefaultClient())
        using (var otherRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session"))
        {
            otherRequest.Headers.Add(
                "Cookie",
                $"{SessionAuthenticationDefaults.CookieName}={otherToken}");
            (await otherClient.SendAsync(otherRequest, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    [Fact]
    public async Task Change_password_rejects_a_wrong_current_password()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);
        var oldHash = await GetStoredHashAsync(user.Id, cancellationToken);

        var response = await ChangePasswordAsync(
            session.Client,
            session.CsrfToken,
            "wrong password entirely",
            NewPassword,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.InvalidCredentials);

        await using var context = fixture.CreateContext();
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.PasswordHash.Should().Be(oldHash);
        (await context.AuthEvents
                .CountAsync(authEvent => authEvent.EventType == AuthEventTypes.PasswordChange, cancellationToken))
            .Should().Be(0);

        // The session survives a rejected change.
        (await session.Client.GetAsync("/api/v1/auth/session", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Change_password_rejects_the_current_password_as_the_new_password()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);
        var oldHash = await GetStoredHashAsync(user.Id, cancellationToken);

        var response = await ChangePasswordAsync(
            session.Client,
            session.CsrfToken,
            Password,
            Password,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("error").GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        root.GetProperty("error").GetProperty("details").GetProperty("field_errors")
            .GetProperty("new_password").EnumerateArray().Should().NotBeEmpty();

        (await GetStoredHashAsync(user.Id, cancellationToken)).Should().Be(oldHash);
    }

    [Theory]
    [InlineData(11, false)]
    [InlineData(12, true)]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Change_password_enforces_the_length_boundaries(int length, bool expectSuccess)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);
        var newPassword = new string('x', length);

        var response = await ChangePasswordAsync(
            session.Client,
            session.CsrfToken,
            Password,
            newPassword,
            cancellationToken);

        if (expectSuccess)
        {
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            new Argon2idPasswordHasher()
                .Verify(await GetStoredHashAsync(user.Id, cancellationToken), newPassword)
                .Should().BeTrue();
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var root = await ReadJsonAsync(response, cancellationToken);
            root.GetProperty("error").GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
            root.GetProperty("error").GetProperty("details").GetProperty("field_errors")
                .GetProperty("new_password").EnumerateArray().Should().NotBeEmpty();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"current_password":"correct horse battery staple"}""")]
    [InlineData("""{"new_password":"a much better passphrase"}""")]
    [InlineData("""{"current_password":"   ","new_password":"a much better passphrase"}""")]
    [InlineData("""{"current_password":"correct horse battery staple","new_password":""}""")]
    [InlineData("""{"current_password":123,"new_password":"a much better passphrase"}""")]
    [InlineData("""{"current_password":"correct horse battery staple","new_password":42}""")]
    public async Task Change_password_validation_failures_return_400(string rawBody)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);

        var response = await ChangePasswordAsync(
            session.Client,
            session.CsrfToken,
            currentPassword: null!,
            newPassword: null!,
            cancellationToken,
            rawBody);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").EnumerateObject().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Change_password_requires_authentication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await ChangePasswordAsync(
            client,
            csrfToken: null,
            Password,
            NewPassword,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.AuthRequired);
        AssertNoStore(response);
    }

    [Fact]
    public async Task Flagged_session_gated_routes_win_over_csrf_but_allowlisted_routes_are_still_validated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken, mustChangePassword: true);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);

        // A gated unsafe route without a CSRF token must report the forced
        // change, not the missing antiforgery token.
        using (var gatedRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household"))
        {
            gatedRequest.Content = new StringContent(
                """{"household_name": "Blocked"}""",
                Encoding.UTF8,
                "application/json");

            var gated = await session.Client.SendAsync(gatedRequest, cancellationToken);
            gated.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorCodeAsync(gated, cancellationToken)).Should().Be(ErrorCodes.PasswordChangeRequired);
        }

        // The allowlisted change-password route passes the gate and is still
        // CSRF-validated.
        using (var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password"))
        {
            changeRequest.Content = new StringContent(
                JsonSerializer.Serialize(new { current_password = Password, new_password = NewPassword }),
                Encoding.UTF8,
                "application/json");

            var change = await session.Client.SendAsync(changeRequest, cancellationToken);
            change.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorCodeAsync(change, cancellationToken)).Should().Be(ErrorCodes.CsrfValidationFailed);
        }
    }

    [Fact]
    public async Task Change_password_does_not_mutate_unrelated_request_cookies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var probe = new CookieProbe();
        using var factory = fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(probe)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username = "smith", password = Password }),
                Encoding.UTF8,
                "application/json"),
        };
        var login = await client.SendAsync(loginRequest, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var sessionToken = ExtractSessionToken(GetSessionCookieHeader(login));
        var antiforgeryCookie = ExtractCookieValue(login, AntiforgeryCookieName);
        var csrfToken = login.Headers.GetValues(CsrfMiddleware.HeaderName).Single();

        var rawCookieHeader =
            $"{SessionAuthenticationDefaults.CookieName}={sessionToken}; " +
            $"{AntiforgeryCookieName}={antiforgeryCookie}; " +
            "keepme=hello%2Fworld; quoted=\"a b\"";

        using var changeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { current_password = Password, new_password = NewPassword }),
                Encoding.UTF8,
                "application/json"),
        };
        changeRequest.Headers.TryAddWithoutValidation("Cookie", rawCookieHeader);
        changeRequest.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);

        var change = await client.SendAsync(changeRequest, cancellationToken);

        change.StatusCode.Should().Be(HttpStatusCode.NoContent);
        probe.RawCookieHeader.Should().Be(
            rawCookieHeader,
            "the raw Cookie header must never be re-serialized from the parsed cookies");
        probe.ParsedCookieNames.Should().Contain("keepme")
            .And.Contain(SessionAuthenticationDefaults.CookieName)
            .And.NotContain(AntiforgeryCookieName);
    }

    [Fact]
    public async Task Auth_responses_disable_caching()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var cookies = new CookieContainer();
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));

        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username = "smith", password = Password }),
                Encoding.UTF8,
                "application/json"),
        };
        var login = await client.SendAsync(loginRequest, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(login);
        var csrfToken = login.Headers.GetValues(CsrfMiddleware.HeaderName).Single();

        var sessionResponse = await client.GetAsync("/api/v1/auth/session", cancellationToken);
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertNoStore(sessionResponse);

        var change = await ChangePasswordAsync(client, csrfToken, Password, NewPassword, cancellationToken);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertNoStore(change);
    }

    [Fact]
    public async Task Forced_change_gate_blocks_gated_routes_and_allows_the_allowlist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken, mustChangePassword: true);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);

        var settingsGet = await session.Client.GetAsync("/api/v1/settings/household", cancellationToken);
        settingsGet.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(settingsGet, cancellationToken)).Should().Be(ErrorCodes.PasswordChangeRequired);

        using (var patchRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household"))
        {
            patchRequest.Content = new StringContent(
                """{"household_name": "Blocked"}""",
                Encoding.UTF8,
                "application/json");
            patchRequest.Headers.Add(CsrfMiddleware.HeaderName, session.CsrfToken);
            var patchResponse = await session.Client.SendAsync(patchRequest, cancellationToken);
            patchResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorCodeAsync(patchResponse, cancellationToken))
                .Should().Be(ErrorCodes.PasswordChangeRequired);
        }

        // Allowlisted routes and public infrastructure stay reachable.
        var sessionResponse = await session.Client.GetAsync("/api/v1/auth/session", cancellationToken);
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sessionRoot = await ReadJsonAsync(sessionResponse, cancellationToken);
        sessionRoot.GetProperty("must_change_password").GetBoolean().Should().BeTrue();

        (await session.Client.GetAsync("/api/v1/health", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await session.Client.GetAsync("/", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Logout stays allowed for a flagged session.
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logoutRequest.Headers.Add(CsrfMiddleware.HeaderName, session.CsrfToken);
        (await session.Client.SendAsync(logoutRequest, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Change_password_clears_the_gate_for_subsequent_requests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken, mustChangePassword: true);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);

        var change = await ChangePasswordAsync(
            session.Client,
            session.CsrfToken,
            Password,
            NewPassword,
            cancellationToken);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await session.Client.GetAsync("/api/v1/settings/household", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var sessionResponse = await session.Client.GetAsync("/api/v1/auth/session", cancellationToken);
        sessionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(sessionResponse, cancellationToken);
        root.GetProperty("must_change_password").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Change_password_writes_no_secret_material_to_events_or_logs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var recorder = new RecordingLoggerProvider();
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(recorder);
            }));
        using var session = await SignInAsync(factory, "smith", Password, cancellationToken);

        var response = await ChangePasswordAsync(
            session.Client,
            session.CsrfToken,
            Password,
            NewPassword,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rotatedToken = ExtractSessionToken(GetSessionCookieHeader(response));

        string newHash;
        List<string> eventTexts;
        await using (var context = fixture.CreateContext())
        {
            newHash = await context.Users
                .AsNoTracking()
                .Where(candidate => candidate.Id == user.Id)
                .Select(candidate => candidate.PasswordHash)
                .SingleAsync(cancellationToken);
            var rows = await context.AuthEvents.AsNoTracking().ToListAsync(cancellationToken);
            eventTexts = rows
                .Select(row => string.Join(
                    '\n',
                    row.EventType,
                    row.UsernameAttempted,
                    row.MetadataJson,
                    row.IpAddress,
                    row.UserAgent))
                .ToList();
        }

        var secrets = new[] { Password, NewPassword, session.RawToken, rotatedToken, newHash };
        foreach (var eventText in eventTexts)
        {
            foreach (var secret in secrets)
            {
                eventText.Should().NotContain(
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

    private sealed record SignedInClient(
        HttpClient Client,
        string CsrfToken,
        string RawToken,
        long SessionId) : IDisposable
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

        var csrfToken = login.Headers.GetValues(CsrfMiddleware.HeaderName).Single();
        var rawToken = ExtractSessionToken(GetSessionCookieHeader(login));

        long sessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var resolved = await scope.ServiceProvider
                .GetRequiredService<SessionService>()
                .ResolveAsync(rawToken, cancellationToken);
            sessionId = resolved!.Session.Id;
        }

        return new SignedInClient(client, csrfToken, rawToken, sessionId);
    }

    private static async Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client,
        string? csrfToken,
        string? currentPassword,
        string? newPassword,
        CancellationToken cancellationToken,
        string? rawBody = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/change-password")
        {
            Content = new StringContent(
                rawBody ?? JsonSerializer.Serialize(new
                {
                    current_password = currentPassword,
                    new_password = newPassword,
                }),
                Encoding.UTF8,
                "application/json"),
        };

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

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
            .CreateAsync(user.Id, "127.0.0.1", TestUserAgent, cancellationToken);

        return (session.Id, rawToken);
    }

    private async Task<string> GetStoredHashAsync(long userId, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Users
            .AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => candidate.PasswordHash)
            .SingleAsync(cancellationToken);
    }

    private async Task<User> SeedUserAsync(
        string username,
        string password,
        CancellationToken cancellationToken,
        bool mustChangePassword = false)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = UserRole.Admin,
            IsActive = true,
            MustChangePassword = mustChangePassword,
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
            HouseholdName = "Password Lifecycle Household",
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

    private static string ExtractSessionToken(string setCookieHeader)
    {
        var value = setCookieHeader[$"{SessionAuthenticationDefaults.CookieName}=".Length..];
        var terminator = value.IndexOf(';');
        return terminator < 0 ? value : value[..terminator];
    }

    private static string ExtractCookieValue(HttpResponseMessage response, string cookieName)
    {
        response.Headers.TryGetValues("Set-Cookie", out var headers).Should().BeTrue();
        var header = headers!.Single(candidate => candidate.StartsWith(
            $"{cookieName}=",
            StringComparison.Ordinal));
        var value = header[$"{cookieName}=".Length..];
        var terminator = value.IndexOf(';');
        return terminator < 0 ? value : value[..terminator];
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    /// <summary>
    /// Captures the raw <c>Cookie</c> header and the parsed cookie names after
    /// the change-password request has completed, proving the rotation re-mint
    /// did not re-serialize or drop unrelated cookies.
    /// </summary>
    private sealed class CookieProbe : IStartupFilter
    {
        public string? RawCookieHeader { get; private set; }

        public IReadOnlyList<string> ParsedCookieNames { get; private set; } = [];

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use(async (context, nextMiddleware) =>
                {
                    await nextMiddleware(context);

                    if (context.Request.Path.StartsWithSegments("/api/v1/auth/change-password"))
                    {
                        RawCookieHeader = context.Request.Headers.Cookie.ToString();
                        ParsedCookieNames = context.Request.Cookies.Keys.ToArray();
                    }
                });

                next(application);
            };
    }
}
