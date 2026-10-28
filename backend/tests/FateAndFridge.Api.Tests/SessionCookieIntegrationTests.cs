using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the opaque session cookie: flags, clearing of
/// invalid cookies, and how the endpoints behave with and without a
/// session (docs/SECURITY.md §2, docs/API.md §21). Requires a Docker daemon
/// run with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class SessionCookieIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Theory]
    [InlineData("http", false)]
    [InlineData("https", true)]
    public async Task Invalid_session_cookie_is_rejected_and_cleared_with_scheme_flags(
        string scheme,
        bool expectSecure)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"{scheme}://localhost"),
            HandleCookies = false,
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/household");
        request.Headers.Add("Cookie", $"{SessionAuthenticationDefaults.CookieName}=not-a-real-session");
        var response = await client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCodeAsync(response, ErrorCodes.AuthRequired, cancellationToken);

        var setCookie = GetSessionSetCookie(response);
        var lowerCaseHeader = setCookie.ToLowerInvariant();

        setCookie.Should().StartWith($"{SessionAuthenticationDefaults.CookieName}=");
        lowerCaseHeader.Should().Contain("httponly");
        lowerCaseHeader.Should().Contain("samesite=lax");
        lowerCaseHeader.Should().Contain("path=/");
        lowerCaseHeader.Contains("secure", StringComparison.Ordinal).Should().Be(expectSecure);
    }

    [Fact]
    public async Task Valid_session_cookie_authenticates_a_multiuser_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();
        var rawToken = await CreateSessionAsync(factory, "smith", cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/household");
        request.Headers.Add("Cookie", $"{SessionAuthenticationDefaults.CookieName}={rawToken}");
        var response = await client.SendAsync(request, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Single_user_mode_requires_no_cookie_and_ignores_a_bogus_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/settings/household", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/household"))
        {
            request.Headers.Add("Cookie", $"{SessionAuthenticationDefaults.CookieName}=bogus");
            var response = await client.SendAsync(request, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var patchResponse = await client.PatchAsync(
            "/api/v1/settings/household",
            new StringContent("""{"household_name": "Single User"}""", Encoding.UTF8, "application/json"),
            cancellationToken);
        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Multiuser_request_without_a_session_returns_auth_required()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/settings/household", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCodeAsync(response, ErrorCodes.AuthRequired, cancellationToken);
    }

    private async Task<string> CreateSessionAsync(
        ApiFactory factory,
        string username,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            PasswordHash = "phc-placeholder",
            Role = UserRole.Member,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        using var scope = factory.Services.CreateScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
        var (_, rawToken) = await sessionService.CreateAsync(
            user.Id,
            "127.0.0.1",
            "cookie-test",
            cancellationToken);

        return rawToken;
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

    private static string GetSessionSetCookie(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var headers).Should().BeTrue();
        return headers!
            .Single(header => header.StartsWith(
                $"{SessionAuthenticationDefaults.CookieName}=",
                StringComparison.Ordinal));
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
    }
}
