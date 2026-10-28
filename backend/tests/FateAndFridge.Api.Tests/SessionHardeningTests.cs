using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Hardening for the session/CSRF surface (issue #20) plus the
/// household-settings concurrency guarantees (issue #19).
/// </summary>
[Collection(SetupCollection.Name)]
public sealed class SessionHardeningTests(SetupFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly Uri LocalBaseAddress = new("http://localhost");

    [Fact]
    public async Task Pre_setup_unsafe_request_with_a_session_cookie_returns_409_before_csrf()
    {
        await fixture.ResetAsync(Ct);
        using var factory = fixture.CreateFactory(CreateTempDataDir());
        var user = await SeedUserAsync("presetup", Ct);
        var cookies = new CookieContainer();
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        await AttachSessionCookieAsync(factory, user, cookies, Ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/household")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", "not-a-real-token");
        var response = await client.SendAsync(request, Ct);

        // Gating precedes CSRF: a missing/invalid token would be 403 otherwise.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await ReadErrorCodeAsync(response);
        error.Should().Be("SETUP_REQUIRED");
    }

    [Fact]
    public async Task Unauthenticated_and_invalid_cookie_responses_omit_the_csrf_header()
    {
        using var factory = await fixture.CreateConfiguredFactoryAsync(Ct);

        using (var anonymous = factory.CreateClient())
        {
            var health = await anonymous.GetAsync("/api/v1/health", Ct);
            health.Headers.Contains("X-CSRF-TOKEN").Should().BeFalse();

            using var unsafeRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            var unauth = await anonymous.SendAsync(unsafeRequest, Ct);
            unauth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            unauth.Headers.Contains("X-CSRF-TOKEN").Should().BeFalse();
        }

        var staleCookies = new CookieContainer();
        staleCookies.Add(
            LocalBaseAddress,
            new Cookie(SessionAuthenticationDefaults.CookieName, "stale-token") { Path = "/" });
        using var staleClient = factory.CreateDefaultClient(new CookieContainerHandler(staleCookies));
        using var staleRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        var stale = await staleClient.SendAsync(staleRequest, Ct);
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        stale.Headers.Contains("X-CSRF-TOKEN").Should().BeFalse();
    }

    [Fact]
    public async Task Antiforgery_token_from_one_user_is_rejected_for_another_user()
    {
        using var factory = await fixture.CreateConfiguredFactoryAsync(Ct);
        var alphaUser = await SeedUserAsync("alpha", Ct);
        var betaUser = await SeedUserAsync("beta", Ct);

        var alphaCookies = new CookieContainer();
        using var alpha = factory.CreateDefaultClient(new CookieContainerHandler(alphaCookies));
        await AttachSessionCookieAsync(factory, alphaUser, alphaCookies, Ct);
        var alphaToken = await MintTokenAsync(alpha);
        alphaToken.Should().NotBeNullOrEmpty();

        var betaCookies = new CookieContainer();
        using var beta = factory.CreateDefaultClient(new CookieContainerHandler(betaCookies));
        await AttachSessionCookieAsync(factory, betaUser, betaCookies, Ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/settings/household")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CSRF-TOKEN", alphaToken);
        var response = await beta.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(response)).Should().Be("CSRF_VALIDATION_FAILED");
    }

    [Fact]
    public async Task Concurrent_session_rotations_keep_exactly_one_last_writer_token()
    {
        await fixture.ResetAsync(Ct);
        using var factory = fixture.CreateFactory(CreateTempDataDir());
        var user = await SeedUserAsync("rotator", Ct);

        long sessionId;
        using (var scope = factory.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var (session, _) = await sessions.CreateAsync(user.Id, "127.0.0.1", "rotation-test", Ct);
            sessionId = session.Id;
        }

        async Task<string?> RotateAsync()
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<SessionService>()
                .RotateAsync(sessionId, Ct);
        }

        var tokens = await Task.WhenAll(RotateAsync(), RotateAsync());
        tokens.Should().OnlyContain(token => token != null);

        await using var context = fixture.CreateContext();
        var rows = await context.UserSessions.Where(row => row.Id == sessionId).ToListAsync(Ct);
        rows.Should().HaveCount(1);

        using (var scope = factory.Services.CreateScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var resolving = 0;
            foreach (var token in tokens)
            {
                resolving += await sessions.ResolveAsync(token, Ct) is null ? 0 : 1;
            }

            // Last writer wins: only the token whose hash was persisted resolves.
            resolving.Should().Be(1);
        }
    }

    [Fact]
    public async Task Data_protection_key_files_are_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await fixture.ResetAsync(Ct);
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);

        // Force key-ring materialization.
        factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("session-hardening")
            .Protect("probe");

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var keyRoot = AppPaths.DataProtectionKeysRoot(configuration);
        var keyFiles = Directory.GetFiles(keyRoot, "key-*.xml");
        keyFiles.Should().NotBeEmpty();

        foreach (var keyFile in keyFiles)
        {
            File.GetUnixFileMode(keyFile).Should()
                .Be(UnixFileMode.UserRead | UnixFileMode.UserWrite, "the key ring is secret material");
        }
    }

    [Fact]
    public async Task Disjoint_household_patches_both_persist_and_same_field_is_last_write_wins()
    {
        await fixture.ResetAsync(Ct);
        using var factory = fixture.CreateFactory(CreateTempDataDir());
        await CompleteSingleUserSetupAsync(factory);

        await ApplyPatchAsync(factory, new HouseholdSettingsPatch("Renamed", null, null, null, null));
        await Task.WhenAll(
            ApplyPatchAsync(factory, new HouseholdSettingsPatch(null, null, 3, null, null)),
            ApplyPatchAsync(factory, new HouseholdSettingsPatch(null, null, null, 6, null)));

        await using (var context = fixture.CreateContext())
        {
            var settings = await context.HouseholdSettings.AsNoTracking().SingleAsync(Ct);
            settings.HouseholdName.Should().Be("Renamed");
            settings.WeekStartsOn.Should().Be(3);
            settings.DefaultDinerCount.Should().Be(6);
        }

        // Same-field concurrent writes are last-write-wins: one of the values
        // wins deterministically as "the last committed write".
        await Task.WhenAll(
            ApplyPatchAsync(factory, new HouseholdSettingsPatch(null, null, null, 5, null)),
            ApplyPatchAsync(factory, new HouseholdSettingsPatch(null, null, null, 7, null)));

        await using (var context = fixture.CreateContext())
        {
            var settings = await context.HouseholdSettings.AsNoTracking().SingleAsync(Ct);
            settings.DefaultDinerCount.Should().BeOneOf(5, 7);
        }
    }

    private static async Task ApplyPatchAsync(ApiFactory factory, HouseholdSettingsPatch patch)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<HouseholdSettingsService>()
            .ApplyPatchAsync(patch, cancellationToken: Ct);
    }

    private static async Task CompleteSingleUserSetupAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<SetupCompletionService>()
            .CompleteAsync(
                new SetupCompletionRequest(
                    "Concurrency Household",
                    "UTC",
                    0,
                    4,
                    StartupPage.Calendar,
                    AuthMode.SingleUser,
                    true,
                    Admin: null,
                    IpAllowlistEnabled: false,
                    IpAllowlistRanges: []),
                ipAddress: null,
                userAgent: null,
                Ct);
        result.Should().NotBeNull();
    }

    private async Task<User> SeedUserAsync(string username, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash("correct horse battery staple"),
            Role = UserRole.Member,
            IsActive = true,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private static async Task AttachSessionCookieAsync(
        ApiFactory factory,
        User user,
        CookieContainer cookies,
        CancellationToken cancellationToken)
    {
        using var scope = factory.Services.CreateScope();
        var (_, rawToken) = await scope.ServiceProvider.GetRequiredService<SessionService>()
            .CreateAsync(user.Id, "127.0.0.1", "session-hardening-tests", cancellationToken);
        cookies.Add(
            LocalBaseAddress,
            new Cookie(SessionAuthenticationDefaults.CookieName, rawToken) { Path = "/" });
    }

    private static async Task<string?> MintTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/session", Ct);
        return response.Headers.TryGetValues("X-CSRF-TOKEN", out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("error").GetProperty("code").GetString();
    }

    private static string CreateTempDataDir()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "fate-and-fridge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
