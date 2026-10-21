using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M13-09 privacy-preserving update check (docs/M13_PLAN.md §1.13–§1.15, §2.2,
/// §4; docs/API.md §25). Requires a Docker daemon for the integration half.
/// <para>
/// The outbound half is asserted against a stub handler: one bodiless GET to
/// the public releases endpoint with a neutral
/// <c>User-Agent: FateAndFridge/&lt;version&gt;</c> and
/// <c>Accept: application/vnd.github+json</c> — nothing else. Drafts and
/// prereleases are ignored, SemVer is compared numerically, the result is
/// cached for 24 hours, and every failure is recorded as a short
/// <c>last_error</c> instead of an application error.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(M13BackupCollection.Name)]
public sealed class M13UpdateCheckTests(M13BackupFixture fixture) : IAsyncLifetime
{
    private const string StatusPath = "/api/v1/updates";
    private const string CheckPath = "/api/v1/admin/updates/check";
    private const string AdminPath = "/api/v1/admin/updates";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m13-updates-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string ReleaseJson = """
        {
          "tag_name": "v0.2.0",
          "html_url": "https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.2.0",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-10-20T18:30:00Z"
        }
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Outbound request privacy ---------------------------------------

    [Fact]
    public async Task The_release_request_is_a_bodiless_get_with_only_the_neutral_identity()
    {
        var handler = new StubReleaseFeedHandler();
        using var client = CreateClient(handler);

        var result = await new GitHubReleaseClient(client).GetLatestReleaseAsync(Ct);

        result.IsSuccess.Should().BeTrue();
        handler.RequestCount.Should().Be(1);

        var request = handler.Requests[0];
        request.Method.Should().Be("GET");
        request.Url.Should().Be(GitHubReleaseClient.LatestReleaseUrl);
        request.HasBody.Should().BeFalse("the update check sends no body");

        request.HeaderValues("User-Agent").Should().Equal($"FateAndFridge/{AppInfo.Version}");
        request.HeaderValues("Accept").Should().Equal("application/vnd.github+json");

        // Exactly the two documented headers: no token, no cookie, no
        // household/account/database identifier, no telemetry header.
        request.HeaderNames.Should().BeEquivalentTo(["Accept", "User-Agent"]);

        client.Timeout.Should().Be(GitHubReleaseClient.RequestTimeout);
    }

    [Fact]
    public async Task The_request_targets_the_public_repository_and_never_a_configured_host()
    {
        // Guards against a future "make the URL configurable" change that could
        // turn the check into a telemetry channel.
        GitHubReleaseClient.LatestReleaseUrl.Should().Be(
            "https://api.github.com/repos/Tadbitnyght/fate-and-fridge/releases/latest");

        var handler = new StubReleaseFeedHandler();
        using var client = CreateClient(handler);
        await new GitHubReleaseClient(client).GetLatestReleaseAsync(Ct);

        handler.Requests.Should().OnlyContain(request =>
            request.Url.StartsWith("https://api.github.com/", StringComparison.Ordinal));
    }

    // ---- SemVer ----------------------------------------------------------

    [Theory]
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("v0.10.0", "0.9.0")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.2.0", "0.1.0")]
    [InlineData("10.0.0", "9.99.99")]
    public void SemVer_compares_components_numerically(string candidate, string current)
    {
        UpdateVersion.IsNewerThan(candidate, current).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.9", "0.2.0")]
    [InlineData("nightly", "0.1.0")]
    [InlineData("1.2", "0.1.0")]
    [InlineData("1.2.3.4", "0.1.0")]
    [InlineData("1.2.3-rc.1", "0.1.0")]
    [InlineData("1.2.3+build.7", "0.1.0")]
    [InlineData("", "0.1.0")]
    [InlineData(null, "0.1.0")]
    public void Unparseable_or_older_tags_never_count_as_an_update(string? candidate, string current)
    {
        UpdateVersion.IsNewerThan(candidate, current).Should().BeFalse();
    }

    [Fact]
    public void SemVer_parsing_handles_the_padded_and_prefixed_forms()
    {
        UpdateVersion.TryParse("v0.10.0", out var parsed).Should().BeTrue();
        parsed.Should().Be(new Version(0, 10, 0));
        UpdateVersion.Normalize("v0.2.0").Should().Be("0.2.0");
        UpdateVersion.Normalize("0.2.0").Should().Be("0.2.0");
    }

    // ---- Payload parsing -------------------------------------------------

    [Fact]
    public void A_published_release_is_read_with_its_normalized_version_and_page()
    {
        ReleaseMetadataParser.TryParse(ReleaseJson, out var release)
            .Should().Be(ReleaseParseOutcome.Release);

        release!.TagName.Should().Be("0.2.0");
        release.ReleaseUrl.Should().Be(
            "https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.2.0");
        release.PublishedAt.Should().Be(new DateTimeOffset(2026, 10, 20, 18, 30, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.2.0","draft":true}""")]
    [InlineData("""{"tag_name":"v0.2.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"banana"}""")]
    [InlineData("""{"tag_name":"v0.2.0-rc.1"}""")]
    [InlineData("""{"draft":false}""")]
    public void Drafts_prereleases_and_unparseable_tags_are_no_release(string json)
    {
        ReleaseMetadataParser.TryParse(json, out var release)
            .Should().Be(ReleaseParseOutcome.NoRelease);
        release.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""["a"]""")]
    [InlineData("")]
    public void A_non_release_payload_is_invalid_and_never_throws(string json)
    {
        ReleaseMetadataParser.TryParse(json, out var release)
            .Should().Be(ReleaseParseOutcome.Invalid);
        release.Should().BeNull();
    }

    [Fact]
    public void A_non_https_release_page_is_not_surfaced()
    {
        ReleaseMetadataParser.TryParse(
                """{"tag_name":"v0.2.0","html_url":"javascript:alert(1)"}""",
                out var release)
            .Should().Be(ReleaseParseOutcome.Release);

        release!.ReleaseUrl.Should().BeNull();
    }

    // ---- Cache, preference, and failures --------------------------------

    [Fact]
    public async Task The_cached_status_is_read_only_and_defaults_to_enabled_before_any_check()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.Client.GetAsync(StatusPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await ReadJsonAsync(response);
        status.GetProperty("current_version").GetString().Should().Be(AppInfo.Version);
        status.GetProperty("latest_version").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("update_available").GetBoolean().Should().BeFalse();
        status.GetProperty("last_checked_at").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("last_error").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("enabled").GetBoolean().Should().BeTrue(
            "an absent update_check_enabled follows the setup default");
        handler.RequestCount.Should().Be(0, "reading the status must not call GitHub");
    }

    [Fact]
    public async Task A_forced_check_records_the_release_and_the_status_reports_it()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.PostAsync(CheckPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await ReadJsonAsync(response);
        status.GetProperty("latest_version").GetString().Should().Be("0.2.0");
        status.GetProperty("update_available").GetBoolean().Should().BeTrue();
        status.GetProperty("release_url").GetString().Should().Be(
            "https://github.com/Tadbitnyght/fate-and-fridge/releases/tag/v0.2.0");
        status.GetProperty("last_error").ValueKind.Should().Be(JsonValueKind.Null);
        DateTimeOffset.TryParse(
            status.GetProperty("last_checked_at").GetString(),
            out _).Should().BeTrue("the check stamps a last-checked time");

        handler.RequestCount.Should().Be(1);

        // The cache rows are the documented application_settings keys.
        await using var context = fixture.CreateContext();
        var stored = await context.ApplicationSettings
            .AsNoTracking()
            .Where(setting => setting.Key.StartsWith("update_check_"))
            .ToDictionaryAsync(setting => setting.Key, setting => setting.ValueJson, Ct);
        stored.Keys.Should().BeEquivalentTo(
        [
            UpdateCheckService.LatestVersionKey,
            UpdateCheckService.CheckedAtKey,
            UpdateCheckService.LastErrorKey,
            UpdateCheckService.ReleaseUrlKey,
        ]);
    }

    [Fact]
    public async Task A_second_check_inside_the_24_hour_window_makes_no_http_request()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);
        using var scope = factory.Services.CreateScope();
        var updateCheck = scope.ServiceProvider.GetRequiredService<UpdateCheckService>();

        var first = await updateCheck.CheckAsync(force: false, Ct);
        first.LatestVersion.Should().Be("0.2.0");
        handler.RequestCount.Should().Be(1);

        var second = await updateCheck.CheckAsync(force: false, Ct);
        second.LatestVersion.Should().Be("0.2.0");
        handler.RequestCount.Should().Be(1, "a cached result is reused for 24 hours");

        // A forced check bypasses the cache.
        await updateCheck.CheckAsync(force: true, Ct);
        handler.RequestCount.Should().Be(2);

        // Once the cache entry is older than 24 hours, a normal check runs again.
        await using (var context = fixture.CreateContext())
        {
            var stale = DateTimeOffset.UtcNow - UpdateCheckService.CacheTtl - TimeSpan.FromMinutes(5);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO application_settings (key, value_json, updated_at)
                VALUES ({UpdateCheckService.CheckedAtKey}, {JsonSerializer.Serialize(stale)}::jsonb, {stale})
                ON CONFLICT (key) DO UPDATE
                SET value_json = EXCLUDED.value_json, updated_at = EXCLUDED.updated_at
                """,
                Ct);
        }

        using var refreshedScope = factory.Services.CreateScope();
        var refreshed = refreshedScope.ServiceProvider.GetRequiredService<UpdateCheckService>();
        await refreshed.CheckAsync(force: false, Ct);
        handler.RequestCount.Should().Be(3);
    }

    [Fact]
    public async Task Disabling_update_checks_stops_every_outbound_request_and_is_persisted()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var patch = await session.PatchAsync(AdminPath, JsonContent(new { enabled = false }));

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(patch)).GetProperty("enabled").GetBoolean().Should().BeFalse();

        // A forced check while disabled still performs no request.
        var check = await session.PostAsync(CheckPath);
        check.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(check)).GetProperty("enabled").GetBoolean().Should().BeFalse();
        handler.RequestCount.Should().Be(0);

        await using var context = fixture.CreateContext();
        var stored = await context.ApplicationSettings
            .AsNoTracking()
            .SingleAsync(setting => setting.Key == UpdateCheckService.EnabledKey, Ct);
        stored.ValueJson.Should().Be("false");

        var audit = await context.AuditEvents
            .AsNoTracking()
            .SingleAsync(row => row.EventType == AuditEventTypes.SettingsUpdateCheckChanged, Ct);
        audit.EntityType.Should().Be(AuditEntityTypes.Settings);
        audit.EntityId.Should().Be(UpdateCheckService.SettingsEntityId);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Http_failures_are_recorded_and_never_throw(int statusCode)
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)statusCode)),
        };
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.PostAsync(CheckPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await ReadJsonAsync(response);
        status.GetProperty("latest_version").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("update_available").GetBoolean().Should().BeFalse();
        status.GetProperty("last_error").GetString().Should().Contain(statusCode.ToString());

        await using var context = fixture.CreateContext();
        var stored = await context.ApplicationSettings
            .AsNoTracking()
            .SingleAsync(setting => setting.Key == UpdateCheckService.LastErrorKey, Ct);
        stored.ValueJson.Should().Contain(statusCode.ToString());
    }

    [Fact]
    public async Task A_timeout_and_an_offline_host_are_recorded_as_a_quiet_last_error()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler
        {
            Responder = (_, _) => throw new TaskCanceledException("simulated timeout"),
        };
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var timeout = await session.PostAsync(CheckPath);
        timeout.StatusCode.Should().Be(HttpStatusCode.OK);
        var timeoutStatus = await ReadJsonAsync(timeout);
        timeoutStatus.GetProperty("last_error").GetString().Should().Be(
            "The release check timed out.");
        timeoutStatus.GetProperty("update_available").GetBoolean().Should().BeFalse();

        handler.Responder = (_, _) => throw new HttpRequestException("simulated offline");
        var offline = await session.PostAsync(CheckPath);
        offline.StatusCode.Should().Be(HttpStatusCode.OK);
        var offlineStatus = await ReadJsonAsync(offline);
        offlineStatus.GetProperty("last_error").GetString().Should().Be(
            "The release feed could not be reached.");
    }

    [Fact]
    public async Task A_failed_check_keeps_the_last_known_release()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        await session.PostAsync(CheckPath);

        handler.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        var failed = await session.PostAsync(CheckPath);

        var status = await ReadJsonAsync(failed);
        status.GetProperty("latest_version").GetString().Should().Be("0.2.0");
        status.GetProperty("release_url").GetString().Should().NotBeNull();
        status.GetProperty("update_available").GetBoolean().Should().BeTrue();
        status.GetProperty("last_error").GetString().Should().Contain("502");
    }

    [Fact]
    public async Task A_draft_or_prerelease_latest_release_is_treated_as_no_release()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler
        {
            Responder = (_, _) => Task.FromResult(JsonResponse(
                """{"tag_name":"v0.3.0","prerelease":true,"draft":false}""")),
        };
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.PostAsync(CheckPath);

        var status = await ReadJsonAsync(response);
        status.GetProperty("latest_version").ValueKind.Should().Be(JsonValueKind.Null);
        status.GetProperty("update_available").GetBoolean().Should().BeFalse();
        status.GetProperty("last_error").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- Permissions -----------------------------------------------------

    [Fact]
    public async Task Update_status_requires_authentication_and_is_readable_by_every_role()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);

        using var anonymous = factory.CreateClient();
        var unauthenticated = await anonymous.GetAsync(StatusPath, Ct);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(unauthenticated)).Should().Be("AUTH_REQUIRED");

        using var member = await SignInAsync(factory, "member", MemberPassword);
        (await member.Client.GetAsync(StatusPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestCount.Should().Be(0, "a member read never triggers the outbound check");
    }

    [Fact]
    public async Task Only_an_admin_may_force_a_check_or_change_the_preference()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);

        using var member = await SignInAsync(factory, "member", MemberPassword);
        var memberCheck = await member.PostAsync(CheckPath);
        memberCheck.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorCodeAsync(memberCheck)).Should().Be("FORBIDDEN");

        var memberPatch = await member.PatchAsync(AdminPath, JsonContent(new { enabled = false }));
        memberPatch.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        handler.RequestCount.Should().Be(0);

        using var admin = await SignInAsync(factory, "root", AdminPassword);
        (await admin.PostAsync(CheckPath)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PatchAsync(AdminPath, JsonContent(new { enabled = true })))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task The_single_user_system_actor_reads_and_forces_the_check()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedSingleUserHouseholdAsync();

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);
        using var client = factory.CreateClient();

        (await client.GetAsync(StatusPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var csrfToken = await MintTokenAsync(client);
        using var check = new HttpRequestMessage(HttpMethod.Post, CheckPath);
        if (csrfToken is not null)
        {
            // The single-user system actor has no session cookie, so the
            // antiforgery guard does not apply; echo a token only if minted.
            check.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }
        (await client.SendAsync(check, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task A_patch_without_a_boolean_enabled_is_a_validation_error()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        var handler = new StubReleaseFeedHandler();
        using var factory = CreateFactory(dataDir, handler);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.PatchAsync(
            AdminPath,
            JsonContent(new { enabled = "yes" }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be("VALIDATION_FAILED");
    }

    // ---- Startup ---------------------------------------------------------

    [Fact]
    public async Task The_startup_check_does_not_block_or_fail_startup()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedSingleUserHouseholdAsync();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubReleaseFeedHandler
        {
            Responder = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return JsonResponse(ReleaseJson);
            },
        };

        using var factory = new ApiFactory(
            fixture.ConnectionString,
            migrateOnStartup: false,
            dataDir,
            services => OverrideReleaseHandler(services, handler),
            liveSyncPublisher: null,
            startupUpdateCheck: true);

        using var client = factory.CreateClient();

        // Startup completed while the release request is still in flight.
        (await client.GetAsync("/api/v1/health", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        release.SetResult();
        await WaitForAsync(() => handler.RequestCount == 1, Ct);
    }

    // ---- Helpers ---------------------------------------------------------

    private static HttpClient CreateClient(StubReleaseFeedHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        GitHubReleaseClient.Configure(client);
        return client;
    }

    private ApiFactory CreateFactory(string dataDir, StubReleaseFeedHandler handler) =>
        fixture.CreateFactory(dataDir, services => OverrideReleaseHandler(services, handler));

    /// <summary>
    /// Replaces the transport of the named release client. The production
    /// registration still applies the neutral request identity, so the stub
    /// observes exactly what GitHub would.
    /// </summary>
    private static void OverrideReleaseHandler(
        IServiceCollection services,
        StubReleaseFeedHandler handler) =>
        services
            .AddHttpClient<GitHubReleaseClient>()
            .ConfigurePrimaryHttpMessageHandler(() => handler);

    private static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The startup update check did not run in time.");
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task SeedMultiuserHouseholdAsync()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await MarkSetupCompletedAsync();
    }

    private async Task SeedSingleUserHouseholdAsync()
    {
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
    }

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M13 Updates Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task MarkSetupCompletedAsync()
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {SeedTimestamp})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            Ct);
    }

    private async Task SeedUserAsync(string username, string password, UserRole role)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password)
    {
        var cookies = new CookieContainerHandler(new CookieContainer());
        var client = factory.CreateDefaultClient(cookies);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);

        var login = await client.SendAsync(request, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        // Unsafe routes echo the session-bound antiforgery token (docs/API.md §5).
        var csrfToken = await MintTokenAsync(client);
        csrfToken.Should().NotBeNullOrEmpty();

        return new SignedInClient(client, csrfToken!);
    }

    private static async Task<string?> MintTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/session", Ct);
        return response.Headers.TryGetValues("X-CSRF-TOKEN", out var values)
            ? values.FirstOrDefault()
            : null;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString()!;

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public Task<HttpResponseMessage> PostAsync(string path, HttpContent? content = null) =>
            SendAsync(HttpMethod.Post, path, content);

        public Task<HttpResponseMessage> PatchAsync(string path, HttpContent content) =>
            SendAsync(HttpMethod.Patch, path, content);

        private async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string path,
            HttpContent? content)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.Add("X-CSRF-TOKEN", CsrfToken);
            return await Client.SendAsync(request, Ct);
        }

        public void Dispose() => Client.Dispose();
    }

    /// <summary>One observed outbound request, captured before the response.</summary>
    private sealed record CapturedRequest(
        string Method,
        string Url,
        IReadOnlyDictionary<string, string[]> Headers,
        bool HasBody)
    {
        public IEnumerable<string> HeaderNames => Headers.Keys;

        public string[] HeaderValues(string name) =>
            Headers.TryGetValue(name, out var values) ? values : [];
    }

    /// <summary>
    /// Stub GitHub Releases transport. It records the exact request the
    /// application makes and can answer, fail, or block on demand.
    /// </summary>
    private sealed class StubReleaseFeedHandler : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<CapturedRequest> _requests = [];

        public Func<CapturedRequest, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }

        public int RequestCount
        {
            get
            {
                lock (_gate)
                {
                    return _requests.Count;
                }
            }
        }

        public IReadOnlyList<CapturedRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var captured = new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                request.Content is not null);

            lock (_gate)
            {
                _requests.Add(captured);
            }

            if (Responder is { } responder)
            {
                return await responder(captured, cancellationToken);
            }

            return JsonResponse(ReleaseJson);
        }
    }
}
