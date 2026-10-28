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

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the IP allowlist and trusted-proxy
/// forwarded headers (docs/SECURITY.md §5, docs/API.md §21): enforcement
/// health/loopback exemptions, IPv6, proxy resolution and spoof protection, and
/// the forwarded-HTTPS Secure-cookie fix. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class IpAllowlistIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string AllowedIp = "203.0.113.10";
    private const string BlockedIp = "198.51.100.5";
    private const string ProxyIp = "203.0.113.1";
    private const string ForwardedClient = "198.51.100.7";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Disabled_allowlist_allows_everything()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(false, [], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await GetAsync(client, "/api/v1/setup/status", BlockedIp, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Enabled_allowlist_allows_covered_clients_and_blocks_the_rest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["203.0.113.0/24"], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await GetAsync(client, "/api/v1/setup/status", AllowedIp, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var blocked = await GetAsync(client, "/api/v1/setup/status", BlockedIp, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(blocked, cancellationToken)).Should().Be(ErrorCodes.IpNotAllowed);
    }

    [Fact]
    public async Task Health_endpoints_are_exempt_from_the_allowlist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["10.0.0.0/8"], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await GetAsync(client, "/api/v1/health", BlockedIp, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/api/v1/health/db", BlockedIp, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Loopback_is_always_allowed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["10.0.0.0/8"], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await GetAsync(client, "/api/v1/setup/status", "127.0.0.1", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/api/v1/setup/status", "::1", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ipv6_ranges_are_matched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["2001:db8::/32"], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await GetAsync(client, "/api/v1/setup/status", "2001:db8::5", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await GetAsync(client, "/api/v1/setup/status", "2001:db9::5", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Blocked_clients_are_rejected_before_setup_gating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["203.0.113.0/24"], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // No setup_completed row: the blocked client must not learn that.
        var blocked = await GetAsync(client, "/api/v1/settings/household", BlockedIp, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(blocked, cancellationToken)).Should().Be(ErrorCodes.IpNotAllowed);

        var allowed = await GetAsync(client, "/api/v1/settings/household", AllowedIp, cancellationToken);
        allowed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(allowed, cancellationToken)).Should().Be(ErrorCodes.SetupRequired);
    }

    [Fact]
    public async Task Trusted_proxy_forwarded_client_address_is_used()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["198.51.100.0/24"], [ProxyIp], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var allowed = await GetAsync(
            client,
            "/api/v1/setup/status",
            ProxyIp,
            cancellationToken,
            forwardedFor: ForwardedClient);
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await GetAsync(
            client,
            "/api/v1/setup/status",
            ProxyIp,
            cancellationToken,
            forwardedFor: "192.0.2.9");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Untrusted_peer_forwarded_headers_are_ignored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["198.51.100.0/24"], [ProxyIp], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // The peer is not a trusted proxy, so the spoofed XFF must not help.
        var response = await GetAsync(
            client,
            "/api/v1/setup/status",
            "203.0.113.9",
            cancellationToken,
            forwardedFor: ForwardedClient);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(response, cancellationToken)).Should().Be(ErrorCodes.IpNotAllowed);
    }

    [Fact]
    public async Task Multi_hop_xff_skips_trusted_proxies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["198.51.100.0/24"], ["203.0.113.0/24"], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var allowed = await GetAsync(
            client,
            "/api/v1/setup/status",
            ProxyIp,
            cancellationToken,
            forwardedFor: $"{ForwardedClient}, 203.0.113.2");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await GetAsync(
            client,
            "/api/v1/setup/status",
            "192.0.2.9",
            cancellationToken,
            forwardedFor: $"{ForwardedClient}, 203.0.113.2");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_when_no_trusted_proxies_are_configured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["203.0.113.0/24"], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // A covered peer cannot use XFF to pretend to be blocked (or anything else).
        var coveredWithSpoofedHeader = await GetAsync(
            client,
            "/api/v1/setup/status",
            AllowedIp,
            cancellationToken,
            forwardedFor: BlockedIp);
        coveredWithSpoofedHeader.StatusCode.Should().Be(HttpStatusCode.OK);

        // An uncovered peer cannot use XFF to enter the allowlist.
        var blockedWithSpoofedHeader = await GetAsync(
            client,
            "/api/v1/setup/status",
            BlockedIp,
            cancellationToken,
            forwardedFor: AllowedIp);
        blockedWithSpoofedHeader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(blockedWithSpoofedHeader, cancellationToken)).Should()
            .Be(ErrorCodes.IpNotAllowed);
    }

    [Fact]
    public async Task Health_stays_exempt_even_with_a_spoofed_forwarded_header()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedSecuritySettingsAsync(true, ["10.0.0.0/8"], [], cancellationToken);
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/v1/health", "/api/v1/health/db" })
        {
            var response = await GetAsync(
                client,
                path,
                BlockedIp,
                cancellationToken,
                forwardedFor: AllowedIp);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} is always exempt", path);
        }
    }

    [Fact]
    public async Task Forwarded_https_from_a_trusted_proxy_makes_the_session_cookie_secure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedSecuritySettingsAsync(false, [], [ProxyIp], cancellationToken);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(
            client,
            ProxyIp,
            cancellationToken,
            forwardedFor: ForwardedClient,
            forwardedProto: "https");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        GetSessionCookieHeader(response).ToLowerInvariant().Should().Contain("secure");
    }

    [Fact]
    public async Task Forwarded_https_from_an_untrusted_peer_does_not_make_the_cookie_secure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedSecuritySettingsAsync(false, [], [ProxyIp], cancellationToken);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await LoginAsync(
            client,
            "203.0.113.9",
            cancellationToken,
            forwardedFor: ForwardedClient,
            forwardedProto: "https");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        GetSessionCookieHeader(response).ToLowerInvariant().Should().NotContain("secure");
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter())));

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string clientIp,
        CancellationToken cancellationToken,
        string? forwardedFor = null,
        string? forwardedProto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, clientIp);
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        if (forwardedProto is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string clientIp,
        CancellationToken cancellationToken,
        string? forwardedFor = null,
        string? forwardedProto = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username = "smith", password = Password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, clientIp);
        if (forwardedFor is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        }

        if (forwardedProto is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task SeedSecuritySettingsAsync(
        bool enabled,
        string[] ranges,
        string[] trustedProxies,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await UpsertAsync(context, SetupCompletionService.IpAllowlistEnabledKey, JsonSerializer.Serialize(enabled), cancellationToken);
        await UpsertAsync(context, SetupCompletionService.IpAllowlistRangesKey, JsonSerializer.Serialize(ranges), cancellationToken);
        await UpsertAsync(context, SetupCompletionService.TrustedProxiesKey, JsonSerializer.Serialize(trustedProxies), cancellationToken);
    }

    private static async Task UpsertAsync(
        Data.AppDbContext context,
        string key,
        string valueJson,
        CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({key}, {valueJson}::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = EXCLUDED.value_json, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Allowlist Household",
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

    private async Task<User> SeedUserAsync(string username, string password, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = UserRole.Admin,
            IsActive = true,
            MustChangePassword = false,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static string GetSessionCookieHeader(HttpResponseMessage response)
    {
        response.Headers.TryGetValues("Set-Cookie", out var headers).Should().BeTrue();
        return headers!
            .Single(header => header.StartsWith(
                $"{SessionAuthenticationDefaults.CookieName}=",
                StringComparison.Ordinal));
    }
}
