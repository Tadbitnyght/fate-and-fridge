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
/// HTTP-level verification of <c>GET/PATCH /api/v1/settings/security</c>
/// (docs/API.md §21): normalization, confirmation and caller-coverage
/// validation, persistence with immediate effect, and the authorization
/// matrix. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class SecuritySettingsIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string CallerIp = "203.0.113.10";
    private const string OtherIp = "198.51.100.5";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Get_returns_normalized_values_for_admins()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);
        await SeedSecuritySettingsAsync(
            enabled: false,
            ranges: ["10.0.0.1", "192.168.0.0/24", "bogus"],
            trustedProxies: ["10.0.0.1"],
            cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await GetAsync(admin.Client, "/api/v1/settings/security", CallerIp, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.GetProperty("ip_allowlist_enabled").GetBoolean().Should().BeFalse();
        root.GetProperty("ip_allowlist_ranges").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal("10.0.0.1/32", "192.168.0.0/24");
        root.GetProperty("trusted_proxies").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal("10.0.0.1/32");
    }

    [Fact]
    public async Task Enabling_requires_confirmation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_enabled":true,"ip_allowlist_ranges":["203.0.113.0/24"]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "confirmed", cancellationToken);
    }

    [Fact]
    public async Task Enabling_requires_at_least_one_range()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_enabled":true,"confirmed":true,"ip_allowlist_ranges":[]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "ip_allowlist_ranges", cancellationToken);
    }

    [Fact]
    public async Task Enabling_requires_caller_coverage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_enabled":true,"confirmed":true,"ip_allowlist_ranges":["10.0.0.0/8"]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "ip_allowlist_ranges", cancellationToken);
    }

    [Theory]
    [InlineData("""{"ip_allowlist_ranges":["10"]}""")]
    [InlineData("""{"ip_allowlist_ranges":["0x7f000001"]}""")]
    [InlineData("""{"ip_allowlist_ranges":["fe80::1%eth0"]}""")]
    [InlineData("""{"ip_allowlist_ranges":["10.0.0.1/33"]}""")]
    [InlineData("""{"ip_allowlist_ranges":["::ffff:10.0.0.0/95"]}""")]
    [InlineData("""{"ip_allowlist_ranges":["::ffff:10.0.0.0/80"]}""")]
    public async Task Invalid_ranges_are_rejected(string json)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            json,
            admin.CsrfToken,
            CallerIp,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "ip_allowlist_ranges", cancellationToken);
    }

    [Fact]
    public async Task Trusted_proxies_normalize_and_reject_invalid_entries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var normalized = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"trusted_proxies":["10.0.0.1"]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);
        normalized.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(normalized, cancellationToken)).GetProperty("trusted_proxies")
            .EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal("10.0.0.1/32");

        var invalid = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"trusted_proxies":["10"]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(invalid, "trusted_proxies", cancellationToken);
    }

    [Fact]
    public async Task Changes_apply_without_restart_and_disabling_needs_no_confirmation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var enable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_enabled":true,"confirmed":true,"ip_allowlist_ranges":["203.0.113.0/24"]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);
        enable.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(enable, cancellationToken)).GetProperty("ip_allowlist_enabled").GetBoolean()
            .Should().BeTrue();

        // Immediately effective for the next request, no restart needed.
        var blocked = await GetAsync(admin.Client, "/api/v1/setup/status", OtherIp, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(blocked, cancellationToken)).Should().Be(ErrorCodes.IpNotAllowed);
        (await GetAsync(admin.Client, "/api/v1/setup/status", CallerIp, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // Disabling needs no confirmation and is immediately effective.
        var disable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_enabled":false}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);
        disable.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(admin.Client, "/api/v1/setup/status", OtherIp, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rejected_mapped_range_patch_leaves_the_allowlist_unchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var enable = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_enabled":true,"confirmed":true,"ip_allowlist_ranges":["203.0.113.0/24"]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);
        enable.StatusCode.Should().Be(HttpStatusCode.OK);

        // A mapped range with a prefix shorter than /96 cannot be represented as
        // an IPv4 range and must be rejected instead of widening to 0.0.0.0/0.
        var rejected = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_ranges":["::ffff:10.0.0.0/80"]}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(rejected, "ip_allowlist_ranges", cancellationToken);

        // Settings are unchanged: 8.8.8.8 stays blocked and the stored range is
        // still the original.
        var blocked = await GetAsync(admin.Client, "/api/v1/setup/status", "8.8.8.8", cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(blocked, cancellationToken)).Should().Be(ErrorCodes.IpNotAllowed);

        var settings = await GetAsync(admin.Client, "/api/v1/settings/security", CallerIp, cancellationToken);
        settings.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(settings, cancellationToken)).GetProperty("ip_allowlist_ranges")
            .EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal("203.0.113.0/24");
    }

    [Fact]
    public async Task Patch_requires_at_least_one_field()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var admin = await SignInAsync(factory, "root", AdminPassword, cancellationToken);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{}""",
            admin.CsrfToken,
            CallerIp,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "body", cancellationToken);
    }

    [Fact]
    public async Task Member_is_forbidden_and_unauthenticated_is_auth_required()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("root", AdminPassword, cancellationToken, UserRole.Admin);
        await SeedUserAsync("jane", MemberPassword, cancellationToken, UserRole.Member);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var member = await SignInAsync(factory, "jane", MemberPassword, cancellationToken);

        var memberGet = await GetAsync(member.Client, "/api/v1/settings/security", CallerIp, cancellationToken);
        memberGet.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(memberGet, cancellationToken)).Should().Be(ErrorCodes.Forbidden);

        var memberPatch = await SendJsonAsync(
            member.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"trusted_proxies":[]}""",
            member.CsrfToken,
            CallerIp,
            cancellationToken);
        memberPatch.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var anonymous = factory.CreateDefaultClient();
        var anonymousGet = await GetAsync(anonymous, "/api/v1/settings/security", CallerIp, cancellationToken);
        anonymousGet.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(anonymousGet, cancellationToken)).Should().Be(ErrorCodes.AuthRequired);
    }

    [Fact]
    public async Task Single_user_system_actor_can_read_and_enable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.SingleUser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await GetAsync(client, "/api/v1/settings/security", CallerIp, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        var enable = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"ip_allowlist_enabled":true,"confirmed":true,"ip_allowlist_ranges":["203.0.113.0/24"]}""",
            csrfToken: null,
            CallerIp,
            cancellationToken);

        enable.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(enable, cancellationToken)).GetProperty("ip_allowlist_enabled").GetBoolean()
            .Should().BeTrue();
    }

    private WebApplicationFactory<Program> CreateFactory() =>
        fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter())));

    private static async Task<HttpResponseMessage> GetAsync(
        HttpClient client,
        string path,
        string clientIp,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, clientIp);
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string json,
        string? csrfToken,
        string clientIp,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, clientIp);
        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, cancellationToken);
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
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, CallerIp);

        var login = await client.SendAsync(request, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        return new SignedInClient(client, login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
    }

    private static async Task AssertFieldErrorAsync(
        HttpResponseMessage response,
        string field,
        CancellationToken cancellationToken)
    {
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty(field, out _).Should().BeTrue();
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
            HouseholdName = "Security Settings Household",
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

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
