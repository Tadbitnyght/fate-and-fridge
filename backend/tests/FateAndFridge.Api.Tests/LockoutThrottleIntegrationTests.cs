using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// HTTP-level verification of the M1-07 part 2 lockout policy and per-IP
/// throttle (docs/SECURITY.md §3): the full 10-failure sequence reaches
/// <c>423 ACCOUNT_LOCKED</c> with an <c>account_locked</c> auth event, the
/// per-IP threshold answers <c>429 RATE_LIMITED</c> with <c>Retry-After</c>,
/// other addresses stay unaffected, and a successful login after lock expiry
/// clears every counter. Concurrency cases cover parallel failures (lost
/// increments) and parallel throttle reservations (check-then-record bypass).
/// Progressive delays are neutralized through the injectable delayer so
/// lockout sequences never sleep. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class LockoutThrottleIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";
    private const string WrongPassword = "wrong password entirely";
    private const string IpA = "203.0.113.50";
    private const string IpB = "203.0.113.51";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Ten_consecutive_failures_lock_the_account_and_write_an_account_locked_event()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(fixture);
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 9; attempt++)
        {
            var failure = await LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken);
            failure.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
            stored.FailedLoginCount.Should().Be(9);
            stored.LockedUntil.Should().BeNull();
            stored.LockoutCount.Should().Be(0);
        }

        // The tenth failure engages the lock but still answers generically.
        var locking = await LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken);
        locking.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(locking, cancellationToken)).Should().Be(ErrorCodes.InvalidCredentials);

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
            stored.FailedLoginCount.Should().Be(10);
            stored.LockoutCount.Should().Be(1);
            stored.LockoutWindowStartedAt.Should().BeCloseTo(
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(1));
            stored.LockedUntil.Should().NotBeNull();
            stored.LockedUntil!.Value.Should().BeCloseTo(
                DateTimeOffset.UtcNow.AddMinutes(LockoutLoginAttemptPolicy.StandardLockMinutes),
                TimeSpan.FromMinutes(1));

            var accountLockedEvents = await context.AuthEvents
                .AsNoTracking()
                .Where(authEvent => authEvent.EventType == AuthEventTypes.AccountLocked)
                .ToListAsync(cancellationToken);
            accountLockedEvents.Should().ContainSingle();
            accountLockedEvents[0].UserId.Should().Be(user.Id);
            accountLockedEvents[0].MetadataJson.Should().Contain("locked_until");
        }

        // While locked, even the correct password is refused before verification.
        var blocked = await LoginAsync(client, "smith", Password, IpA, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Locked);
        var root = await ReadJsonAsync(blocked, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.AccountLocked);
        var reportedLockedUntil = DateTimeOffset.Parse(
            error.GetProperty("details").GetProperty("locked_until").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        reportedLockedUntil.ToUniversalTime().Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddMinutes(LockoutLoginAttemptPolicy.StandardLockMinutes).ToUniversalTime(),
            TimeSpan.FromMinutes(1));

        // Rejected attempts while locked are login failures, not new locks.
        await using var verification = fixture.CreateContext();
        (await verification.AuthEvents
                .CountAsync(authEvent => authEvent.EventType == AuthEventTypes.AccountLocked, cancellationToken))
            .Should().Be(1);
        var failureMetadata = await verification.AuthEvents
            .AsNoTracking()
            .Where(authEvent => authEvent.EventType == AuthEventTypes.LoginFailure)
            .Select(authEvent => authEvent.MetadataJson)
            .ToListAsync(cancellationToken);
        failureMetadata.Should().Contain(
            metadata => metadata != null && metadata.Contains(AuthService.AccountLockedReason));
    }

    [Fact]
    public async Task Ten_parallel_failures_are_counted_atomically_and_lock_the_account()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(fixture);
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken)));

        responses.Select(response => response.StatusCode)
            .Should().OnlyContain(status => status == HttpStatusCode.Unauthorized);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
            stored.FailedLoginCount.Should().Be(10, "parallel failures must not lose increments");
            stored.LockoutCount.Should().Be(1);
            stored.LockoutWindowStartedAt.Should().BeCloseTo(
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(1));
            stored.LockedUntil.Should().NotBeNull();
            stored.LockedUntil!.Value.Should().BeCloseTo(
                DateTimeOffset.UtcNow.AddMinutes(LockoutLoginAttemptPolicy.StandardLockMinutes),
                TimeSpan.FromMinutes(1));

            (await context.AuthEvents
                    .CountAsync(authEvent => authEvent.EventType == AuthEventTypes.AccountLocked, cancellationToken))
                .Should().Be(1);
        }

        var blocked = await LoginAsync(client, "smith", Password, IpA, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Locked);
        blocked.Dispose();
    }

    [Fact]
    public async Task Twenty_parallel_failures_engage_exactly_one_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(
            fixture,
            services => services.PostConfigure<LoginThrottleOptions>(options =>
            {
                // Keep the per-IP throttle clear of the 20-attempt burst.
                options.MaxFailuresPerWindow = 100;
            }));
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken)));

        responses.Select(response => response.StatusCode)
            .Should().OnlyContain(status =>
                status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Locked);
        var acceptedFailures = responses.Count(
            response => response.StatusCode == HttpStatusCode.Unauthorized);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        acceptedFailures.Should().BeGreaterThanOrEqualTo(
            LockoutLoginAttemptPolicy.LockoutFailureThreshold,
            "the burst must cross the lock threshold");

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.Users
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
            stored.FailedLoginCount.Should().Be(
                acceptedFailures,
                "every accepted failure is counted exactly once, with none lost");
            stored.LockoutCount.Should().Be(1, "a parallel burst engages exactly one lock");
            stored.LockedUntil.Should().NotBeNull();
            stored.LockedUntil!.Value.Should().BeCloseTo(
                DateTimeOffset.UtcNow.AddMinutes(LockoutLoginAttemptPolicy.StandardLockMinutes),
                TimeSpan.FromMinutes(1));

            (await context.AuthEvents
                    .CountAsync(authEvent => authEvent.EventType == AuthEventTypes.AccountLocked, cancellationToken))
                .Should().Be(1, "only the crossing failure writes an account_locked event");
        }

        var blocked = await LoginAsync(client, "smith", Password, IpA, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.Locked);
        blocked.Dispose();
    }

    [Fact]
    public async Task Nine_existing_failures_then_a_parallel_burst_engages_exactly_one_new_lock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            "smith",
            Password,
            cancellationToken,
            failedLoginCount: 9);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(
            fixture,
            services => services.PostConfigure<LoginThrottleOptions>(options =>
            {
                options.MaxFailuresPerWindow = 100;
            }));
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken)));

        responses.Select(response => response.StatusCode)
            .Should().OnlyContain(status =>
                status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Locked);
        var acceptedFailures = responses.Count(
            response => response.StatusCode == HttpStatusCode.Unauthorized);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        acceptedFailures.Should().BeGreaterThanOrEqualTo(
            1,
            "the seeded nine failures make every accepted attempt a crossing candidate");

        await using var context = fixture.CreateContext();
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(
            9 + acceptedFailures,
            "every accepted failure is counted exactly once, with none lost");
        stored.LockoutCount.Should().Be(1, "the burst contains exactly one crossing failure");
        stored.LockedUntil.Should().NotBeNull();
        stored.LockedUntil!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddMinutes(LockoutLoginAttemptPolicy.StandardLockMinutes),
            TimeSpan.FromMinutes(1));
        (await context.AuthEvents
                .CountAsync(authEvent => authEvent.EventType == AuthEventTypes.AccountLocked, cancellationToken))
            .Should().Be(1);
    }

    [Fact]
    public async Task Second_lock_engagement_increments_the_lockout_count_within_the_same_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var windowStartedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            "smith",
            Password,
            cancellationToken,
            failedLoginCount: 9,
            lockoutCount: 1,
            lockoutWindowStartedAt: windowStartedAt,
            lockedUntil: null);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(fixture);
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Dispose();

        await using var context = fixture.CreateContext();
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(10);
        stored.LockoutCount.Should().Be(2, "a second lock inside the window escalates the count");
        stored.LockoutWindowStartedAt.Should().BeCloseTo(windowStartedAt, TimeSpan.FromMilliseconds(1));
        stored.LockedUntil.Should().NotBeNull();
        stored.LockedUntil!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow.AddMinutes(LockoutLoginAttemptPolicy.StandardLockMinutes),
            TimeSpan.FromMinutes(1));
        (await context.AuthEvents
                .CountAsync(authEvent => authEvent.EventType == AuthEventTypes.AccountLocked, cancellationToken))
            .Should().Be(1);
    }

    [Fact]
    public async Task Parallel_failures_reserve_exactly_the_threshold_and_rate_limit_the_rest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(
            fixture,
            services => services.PostConfigure<LoginThrottleOptions>(options =>
            {
                options.MaxFailuresPerWindow = 3;
                options.WindowSeconds = 60;
            }));
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            LoginAsync(client, "ghost", WrongPassword, IpA, cancellationToken)));

        responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized)
            .Should().Be(3, "exactly the threshold reservations pass through to verification");
        responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests)
            .Should().Be(9);

        var blocked = responses.First(response => response.StatusCode == HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(blocked, cancellationToken)).Should().Be(ErrorCodes.RateLimited);
        blocked.Headers.TryGetValues("Retry-After", out var retryAfterValues).Should().BeTrue();
        int.Parse(retryAfterValues!.Single(), CultureInfo.InvariantCulture)
            .Should().BeInRange(1, 60);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Throttled_attempts_emit_one_warning_each_without_auth_event_writes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var recorder = new RecordingLoggerProvider();
        using var factory = CreateFactory(
            fixture,
            services => services.PostConfigure<LoginThrottleOptions>(options =>
            {
                options.MaxFailuresPerWindow = 1;
                options.WindowSeconds = 60;
            }),
            recorder);
        using var client = factory.CreateClient();

        (await LoginAsync(client, "ghost", WrongPassword, IpA, cancellationToken)).Dispose();
        (await LoginAsync(client, "ghost", WrongPassword, IpA, cancellationToken)).Dispose();
        var blocked = await LoginAsync(client, "ghost", WrongPassword, IpA, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        blocked.Dispose();

        recorder.Messages
            .Count(message => message.Contains("rate limited", StringComparison.Ordinal))
            .Should().Be(2, "one warning per blocked 429 and none for the recorded failure");

        await using var context = fixture.CreateContext();
        (await context.AuthEvents.CountAsync(cancellationToken))
            .Should().Be(1, "throttled requests write no auth events");
    }

    [Fact]
    public async Task Per_ip_threshold_returns_429_with_retry_after_and_other_addresses_are_unaffected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(
            fixture,
            services => services.PostConfigure<LoginThrottleOptions>(options =>
            {
                options.MaxFailuresPerWindow = 3;
                options.WindowSeconds = 60;
            }));
        using var client = factory.CreateClient();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var failure = await LoginAsync(client, "ghost", WrongPassword, IpA, cancellationToken);
            failure.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var blocked = await LoginAsync(client, "ghost", WrongPassword, IpA, cancellationToken);
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(blocked, cancellationToken)).Should().Be(ErrorCodes.RateLimited);

        blocked.Headers.TryGetValues("Retry-After", out var retryAfterValues).Should().BeTrue();
        var retryAfter = int.Parse(retryAfterValues!.Single(), CultureInfo.InvariantCulture);
        retryAfter.Should().BeInRange(1, 60);

        var otherAddress = await LoginAsync(client, "ghost", WrongPassword, IpB, cancellationToken);
        otherAddress.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The throttled request is not audited: no write amplification under abuse.
        await using var context = fixture.CreateContext();
        (await context.AuthEvents.CountAsync(cancellationToken)).Should().Be(4);
    }

    [Fact]
    public async Task Successful_login_clears_the_ip_failure_counter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await SeedUserAsync("smith", Password, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(
            fixture,
            services => services.PostConfigure<LoginThrottleOptions>(options =>
            {
                options.MaxFailuresPerWindow = 3;
                options.WindowSeconds = 60;
            }));

        // The successful login must not leave a session cookie on this client:
        // authenticated unsafe requests would then require X-CSRF-TOKEN.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        (await LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await LoginAsync(client, "smith", Password, IpA, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // The success cleared the two earlier failures: three new ones fit.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
        }

        (await LoginAsync(client, "smith", WrongPassword, IpA, cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Successful_login_after_lock_expiry_clears_all_lockout_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        var user = await SeedUserAsync(
            "smith",
            Password,
            cancellationToken,
            failedLoginCount: 10,
            lockoutCount: 2,
            lockoutWindowStartedAt: DateTimeOffset.UtcNow.AddHours(-1),
            lockedUntil: DateTimeOffset.UtcNow.AddMinutes(-1));
        await MarkSetupCompletedAsync(cancellationToken);

        using var factory = CreateFactory(fixture);
        using var client = factory.CreateClient();

        var response = await LoginAsync(client, "smith", Password, IpA, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var stored = await context.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(0);
        stored.LastFailedLoginAt.Should().BeNull();
        stored.LockoutCount.Should().Be(0);
        stored.LockoutWindowStartedAt.Should().BeNull();
        stored.LockedUntil.Should().BeNull();
    }

    private static WebApplicationFactory<Program> CreateFactory(
        M1SetupFixture fixture,
        Action<IServiceCollection>? configureServices = null,
        RecordingLoggerProvider? loggerProvider = null) =>
        fixture.CreateFactory().WithWebHostBuilder(builder =>
        {
            if (loggerProvider is not null)
            {
                builder.ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(loggerProvider);
                });
            }

            builder.ConfigureServices(services =>
            {
                // No real progressive-delay sleeps, and a deterministic client
                // address driven by a test-only request header.
                services.RemoveAll<ILoginAttemptDelayer>();
                services.AddSingleton<ILoginAttemptDelayer, RecordingLoginAttemptDelayer>();
                services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter());
                configureServices?.Invoke(services);
            });
        });

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string username,
        string password,
        string ipAddress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, ipAddress);
        request.Headers.TryAddWithoutValidation("User-Agent", "lockout-throttle-test");

        return await client.SendAsync(request, cancellationToken);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Lockout Test Household",
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
        int failedLoginCount = 0,
        int lockoutCount = 0,
        DateTimeOffset? lockoutWindowStartedAt = null,
        DateTimeOffset? lockedUntil = null)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = UserRole.Admin,
            IsActive = true,
            FailedLoginCount = failedLoginCount,
            LastFailedLoginAt = failedLoginCount > 0 ? SeedTimestamp : null,
            LockoutCount = lockoutCount,
            LockoutWindowStartedAt = lockoutWindowStartedAt,
            LockedUntil = lockedUntil,
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

    /// <summary>
    /// Sets the client address from a test-only header so one test host can
    /// exercise several source IPs; the header is absent in production.
    /// </summary>
    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public const string HeaderName = "X-Test-Remote-IP";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            application =>
            {
                application.Use((context, nextMiddleware) =>
                {
                    var header = context.Request.Headers[HeaderName].ToString();
                    if (!string.IsNullOrWhiteSpace(header))
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(header);
                    }

                    return nextMiddleware(context);
                });

                next(application);
            };
    }
}
