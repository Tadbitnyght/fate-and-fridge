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
/// HTTP-level verification of the auth-mode switch (docs/API.md §21)
/// single-user → multiuser conversion with the first Admin signed in, repeat
/// and downgrade responses, validation, and the concurrent-switch race.
/// Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class AuthModeSwitchIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string AdminPassword = "root horse battery staple";
    private const string TestUserAgent = "auth-mode-switch-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Single_user_switch_creates_the_first_admin_and_signs_them_in()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        var cookies = new CookieContainer();
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));

        var response = await SendJsonAsync(
            client,
            "/api/v1/settings/auth-mode",
            """{"admin":{"username":"root","password":"root horse battery staple"}}""",
            csrfToken: null,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        root.GetProperty("auth_mode").GetString().Should().Be("multiuser");
        root.GetProperty("user").GetProperty("username").GetString().Should().Be("root");
        root.GetProperty("user").GetProperty("role").GetString().Should().Be("admin");
        root.GetProperty("must_change_password").GetBoolean().Should().BeFalse();
        root.GetProperty("permissions").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.All);

        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        setCookies.Should().Contain(header => header.StartsWith(
            $"{SessionAuthenticationDefaults.CookieName}=",
            StringComparison.Ordinal));
        setCookies.Should().Contain(header => header.StartsWith("faf_csrf=", StringComparison.Ordinal));
        response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out _).Should().BeTrue();

        await using (var context = fixture.CreateContext())
        {
            var settings = await context.HouseholdSettings.AsNoTracking().SingleAsync(cancellationToken);
            settings.AuthMode.Should().Be(AuthMode.Multiuser);

            var admin = await context.Users.AsNoTracking().SingleAsync(cancellationToken);
            admin.Username.Should().Be("root");
            admin.NormalizedUsername.Should().Be("root");
            admin.Role.Should().Be(UserRole.Admin);
            admin.IsActive.Should().BeTrue();
            admin.MustChangePassword.Should().BeFalse();
            admin.LastLoginAt.Should().NotBeNull();
            new Argon2idPasswordHasher().Verify(admin.PasswordHash, AdminPassword).Should().BeTrue();
        }

        var statusResponse = await client.GetAsync("/api/v1/setup/status", cancellationToken);
        var status = await ReadJsonAsync(statusResponse, cancellationToken);
        status.GetProperty("setup_completed").GetBoolean().Should().BeTrue();
        status.GetProperty("auth_mode").GetString().Should().Be("multiuser");

        // The established session reaches Admin-only routes immediately.
        (await client.GetAsync("/api/v1/users", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Normal login with the new credentials also works.
        using var anonymousClient = factory.CreateDefaultClient();
        (await LoginAsync(anonymousClient, "root", AdminPassword, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Switch_when_already_multiuser_returns_409()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            "/api/v1/settings/auth-mode",
            """{"admin":{"username":"second","password":"second admin password"}}""",
            admin.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.AuthModeAlreadyMultiuser);

        await using var context = fixture.CreateContext();
        (await context.Users.CountAsync(cancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Explicit_single_user_request_returns_downgrade_not_supported()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            "/api/v1/settings/auth-mode",
            """{"auth_mode":"single_user"}""",
            admin.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(response, cancellationToken))
            .Should().Be(ErrorCodes.AuthModeDowngradeNotSupported);
    }

    [Theory]
    [InlineData("{}", "admin")]
    [InlineData("""{"admin":{"username":"ab","password":"long enough password"}}""", "admin.username")]
    [InlineData("""{"admin":{"username":"root","password":"short"}}""", "admin.password")]
    [InlineData("""{"admin":"root"}""", "admin")]
    [InlineData("""{"auth_mode":"multi","admin":{"username":"root","password":"long enough password"}}""", "auth_mode")]
    [InlineData("not json", "body")]
    public async Task Switch_validation_failures_return_400_with_the_field(string json, string field)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            "/api/v1/settings/auth-mode",
            json,
            csrfToken: null,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty(field, out _).Should().BeTrue();

        await using var context = fixture.CreateContext();
        (await context.Users.CountAsync(cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_switches_create_exactly_one_admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            SendJsonAsync(
                client,
                "/api/v1/settings/auth-mode",
                """{"admin":{"username":"root","password":"root horse battery staple"}}""",
                csrfToken: null,
                cancellationToken),
            SendJsonAsync(
                client,
                "/api/v1/settings/auth-mode",
                """{"admin":{"username":"root","password":"root horse battery staple"}}""",
                csrfToken: null,
                cancellationToken));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
            var conflict = responses.Single(response => response.StatusCode == HttpStatusCode.Conflict);
            (await ReadErrorCodeAsync(conflict, cancellationToken)).Should().Be(ErrorCodes.AuthModeAlreadyMultiuser);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var context = fixture.CreateContext();
        (await context.Users.CountAsync(cancellationToken)).Should().Be(1);
        (await context.HouseholdSettings.AsNoTracking().SingleAsync(cancellationToken)).AuthMode
            .Should().Be(AuthMode.Multiuser);
    }

    [Fact]
    public async Task Already_multiuser_returns_409_before_validating_the_admin_block()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        // No admin block: the repeat response wins over body validation.
        var response = await SendJsonAsync(
            admin.Client,
            "/api/v1/settings/auth-mode",
            """{}""",
            admin.CsrfToken,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.AuthModeAlreadyMultiuser);
    }

    [Fact]
    public async Task Concurrent_switch_and_user_create_with_the_same_username_do_not_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(
            SendJsonAsync(
                client,
                "/api/v1/settings/auth-mode",
                """{"admin":{"username":"root","password":"root horse battery staple"}}""",
                csrfToken: null,
                cancellationToken),
            SendJsonAsync(
                client,
                "/api/v1/users",
                """{"username":"root","password":"root horse battery staple"}""",
                csrfToken: null,
                cancellationToken));

        try
        {
            responses.Count(response =>
                    response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                .Should().Be(1);
            var failed = responses.Single(response =>
                response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created));
            failed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ReadErrorCodeAsync(failed, cancellationToken)).Should().Be(ErrorCodes.ValidationFailed);
            (await ReadJsonAsync(failed, cancellationToken))
                .GetProperty("error").GetProperty("details").GetProperty("field_errors")
                .EnumerateObject().Should().NotBeEmpty();
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var context = fixture.CreateContext();
        (await context.Users.CountAsync(cancellationToken)).Should().Be(1);
        (await context.Users.CountAsync(
                candidate => candidate.NormalizedUsername == "root",
                cancellationToken))
            .Should().Be(1);
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
        string path,
        string json,
        string? csrfToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
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

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Auth Mode Household",
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
        UserRole role)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
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
