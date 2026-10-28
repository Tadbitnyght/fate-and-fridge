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
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the Admin password reset (docs/API.md §5
/// docs/SECURITY.md §2/§17): one-time temporary passwords, authorization
/// target-session revocation, the forced-change gate, and secret hygiene.
/// Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class AdminResetPasswordIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string AdminPassword = "root horse battery staple";
    private const string TargetPassword = "correct horse battery staple";
    private const string TestUserAgent = "admin-reset-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Admin_reset_returns_a_temporary_password_gates_the_target_and_invalidates_the_old_password()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var target = await SeedUserAsync("smith", TargetPassword, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var adminSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var (targetSessionId, targetTokenBefore) = await CreateSessionAsync(factory, target, cancellationToken);
        var oldHash = await GetStoredHashAsync(target.Id, cancellationToken);

        var response = await ResetPasswordAsync(
            adminSession.Client,
            adminSession.CsrfToken,
            target.Id,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using (var payload = JsonDocument.Parse(body))
        {
            payload.RootElement.EnumerateObject().Select(property => property.Name)
                .Should().Equal("temporary_password");
        }

        var temporaryPassword = JsonDocument.Parse(body).RootElement
            .GetProperty("temporary_password").GetString()!;
        temporaryPassword.Length.Should().Be(TemporaryPasswordGenerator.Length);
        PasswordPolicy.IsValid(temporaryPassword).Should().BeTrue();
        CountOccurrences(body, temporaryPassword).Should().Be(1, "the temporary password is returned exactly once");

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == target.Id, cancellationToken);
            stored.MustChangePassword.Should().BeTrue();
            stored.PasswordHash.Should().NotBe(oldHash);
            new Argon2idPasswordHasher().Verify(stored.PasswordHash, temporaryPassword).Should().BeTrue();
            stored.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

            var revokedSession = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == targetSessionId, cancellationToken);
            revokedSession.RevokedAt.Should().NotBeNull();
            revokedSession.RevokedReason.Should().Be(AuthService.PasswordResetRevocationReason);

            var resetEvent = await context.AuthEvents
                .AsNoTracking()
                .SingleAsync(
                    authEvent => authEvent.EventType == AuthEventTypes.PasswordReset,
                    cancellationToken);
            resetEvent.UserId.Should().Be(target.Id);
            resetEvent.UsernameAttempted.Should().Be("smith");
            resetEvent.UserAgent.Should().Be(TestUserAgent);
            resetEvent.MetadataJson.Should().Contain("actor_user_id");
            resetEvent.MetadataJson.Should().Contain(admin.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // The pre-reset session is revoked.
        using (var staleClient = factory.CreateDefaultClient())
        using (var staleRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/session"))
        {
            staleRequest.Headers.Add(
                "Cookie",
                $"{SessionAuthenticationDefaults.CookieName}={targetTokenBefore}");
            (await staleClient.SendAsync(staleRequest, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        // The old password no longer works; the temporary password does.
        using (var anonymousClient = factory.CreateDefaultClient())
        {
            (await LoginAsync(anonymousClient, "smith", TargetPassword, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        // The temporary session is gated until the password is changed.
        var temporaryCookies = new CookieContainer();
        using var temporaryClient = factory.CreateDefaultClient(new CookieContainerHandler(temporaryCookies));
        using var temporaryRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username = "smith", password = temporaryPassword }),
                Encoding.UTF8,
                "application/json"),
        };
        var temporaryLogin = await temporaryClient.SendAsync(temporaryRequest, cancellationToken);
        temporaryLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginRoot = await ReadJsonAsync(temporaryLogin, cancellationToken);
        loginRoot.GetProperty("must_change_password").GetBoolean().Should().BeTrue();

        var gated = await temporaryClient.GetAsync("/api/v1/settings/household", cancellationToken);
        gated.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(gated, cancellationToken)).Should().Be(ErrorCodes.PasswordChangeRequired);
    }

    [Fact]
    public async Task Admin_reset_clears_lockout_and_the_temporary_password_logs_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var target = await SeedUserAsync(
            "smith",
            TargetPassword,
            cancellationToken,
            lockedUntil: DateTimeOffset.UtcNow.AddMinutes(15),
            failedLoginCount: 10,
            lockoutCount: 2,
            lockoutWindowStartedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var adminSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        // The account is locked before the reset...
        using (var lockedClient = factory.CreateDefaultClient())
        {
            (await LoginAsync(lockedClient, "smith", TargetPassword, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Locked);
        }

        var response = await ResetPasswordAsync(
            adminSession.Client,
            adminSession.CsrfToken,
            target.Id,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var temporaryPassword = (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("temporary_password").GetString()!;

        // and every lockout counter is cleared by the reset.
        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == target.Id, cancellationToken);
            stored.FailedLoginCount.Should().Be(0);
            stored.LastFailedLoginAt.Should().BeNull();
            stored.LockedUntil.Should().BeNull();
            stored.LockoutCount.Should().Be(0);
            stored.LockoutWindowStartedAt.Should().BeNull();
            stored.MustChangePassword.Should().BeTrue();
        }

        // The temporary password logs in without 423 and lands gated.
        using var anonymousClient = factory.CreateDefaultClient();
        var temporaryLogin = await LoginAsync(anonymousClient, "smith", temporaryPassword, cancellationToken);
        temporaryLogin.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginRoot = await ReadJsonAsync(temporaryLogin, cancellationToken);
        loginRoot.GetProperty("must_change_password").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Reset_password_rejects_non_admins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await SeedUserAsync("jane", TargetPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var memberSession = await SignInAsync(factory, "jane", TargetPassword, cancellationToken);

        // The target id need not exist: authorization is checked first.
        var response = await ResetPasswordAsync(
            memberSession.Client,
            memberSession.CsrfToken,
            targetUserId: 424242,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task Reset_password_requires_authentication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await ResetPasswordAsync(client, csrfToken: null, targetUserId: 1, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Reset_password_returns_404_for_an_unknown_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var adminSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await ResetPasswordAsync(
            adminSession.Client,
            adminSession.CsrfToken,
            targetUserId: 999_999,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.NotFound);
    }

    [Fact]
    public async Task Reset_password_for_an_inactive_user_succeeds_but_login_stays_blocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var target = await SeedUserAsync(
            "ghost",
            TargetPassword,
            cancellationToken,
            isActive: false);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var adminSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var (targetSessionId, _) = await CreateSessionAsync(factory, target, cancellationToken);

        var response = await ResetPasswordAsync(
            adminSession.Client,
            adminSession.CsrfToken,
            target.Id,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var temporaryPassword = (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("temporary_password").GetString()!;

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == target.Id, cancellationToken);
            stored.MustChangePassword.Should().BeTrue();
            stored.IsActive.Should().BeFalse();
            new Argon2idPasswordHasher().Verify(stored.PasswordHash, temporaryPassword).Should().BeTrue();

            var revokedSession = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == targetSessionId, cancellationToken);
            revokedSession.RevokedAt.Should().NotBeNull();
        }

        // Both the old and the temporary password fail generically while inactive.
        using var anonymousClient = factory.CreateDefaultClient();
        (await LoginAsync(anonymousClient, "ghost", TargetPassword, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(anonymousClient, "ghost", temporaryPassword, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reset_password_temporary_secret_never_reaches_logs_or_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var recorder = new RecordingLoggerProvider();
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var admin = await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        var target = await SeedUserAsync("smith", TargetPassword, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(recorder);
            }));
        using var adminSession = await SignInAsync(factory, "root", AdminPassword, cancellationToken);
        var (_, targetTokenBefore) = await CreateSessionAsync(factory, target, cancellationToken);

        var response = await ResetPasswordAsync(
            adminSession.Client,
            adminSession.CsrfToken,
            target.Id,
            cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var temporaryPassword = (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("temporary_password").GetString()!;

        string newHash;
        List<string> eventTexts;
        await using (var context = fixture.CreateContext())
        {
            newHash = await context.Users
                .AsNoTracking()
                .Where(candidate => candidate.Id == target.Id)
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

        eventTexts.Should().NotContain(text => text.Contains(temporaryPassword, StringComparison.Ordinal));
        newHash.Should().NotBe(temporaryPassword);

        var secrets = new[]
        {
            temporaryPassword,
            TargetPassword,
            AdminPassword,
            adminSession.RawToken,
            targetTokenBefore,
            newHash,
        };
        foreach (var secret in secrets)
        {
            recorder.AllText.Should().NotContain(
                text => text.Contains(secret, StringComparison.Ordinal),
                "logs must never contain password, hash, or token material");
        }
    }

    [Fact]
    public async Task Single_user_system_actor_passes_authorization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        // No session cookie: the system actor is allowed, and the unknown target
        // is the only reason for the 404 (a 401/403 would mean the actor was rejected).
        var response = await ResetPasswordAsync(client, csrfToken: null, targetUserId: 1, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.NotFound);
    }

    private sealed record SignedInClient(
        HttpClient Client,
        string CsrfToken,
        string RawToken) : IDisposable
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
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single(),
            ExtractSessionToken(GetSessionCookieHeader(login)));
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> ResetPasswordAsync(
        HttpClient client,
        string? csrfToken,
        long targetUserId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/users/{targetUserId}/reset-password");
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);
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
        UserRole role = UserRole.Member,
        bool isActive = true,
        bool mustChangePassword = false,
        DateTimeOffset? lockedUntil = null,
        int failedLoginCount = 0,
        int lockoutCount = 0,
        DateTimeOffset? lockoutWindowStartedAt = null)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = isActive,
            MustChangePassword = mustChangePassword,
            LockedUntil = lockedUntil,
            FailedLoginCount = failedLoginCount,
            LastFailedLoginAt = failedLoginCount > 0 ? SeedTimestamp : null,
            LockoutCount = lockoutCount,
            LockoutWindowStartedAt = lockoutWindowStartedAt,
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
            HouseholdName = "Admin Reset Household",
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

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
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
}
