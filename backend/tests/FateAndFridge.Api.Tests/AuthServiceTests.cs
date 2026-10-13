using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of <see cref="AuthService"/> beyond the HTTP
/// surface: the unknown-user timing-equalization verify, lock short-circuiting
/// before password work, and logout revocation. Requires a Docker daemon; run
/// with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class AuthServiceTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string Password = "correct horse battery staple";

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Unknown_user_login_still_performs_one_dummy_argon2id_verification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var hasher = new RecordingPasswordHasher();
        await using var context = fixture.CreateContext();
        var service = CreateService(context, hasher, clock);

        var result = await service.LoginAsync(
            "  Ghost  ",
            Password,
            "203.0.113.7",
            "auth-service-test",
            cancellationToken);

        result.Should().BeOfType<LoginResult.InvalidCredentials>();

        // The dummy hash is a canonical production-parameter PHC string, and
        // the supplied password is what gets verified against it.
        var verifyCall = hasher.VerifyCalls.Should().ContainSingle().Subject;
        verifyCall.Hash.Should().StartWith("$argon2id$v=19$m=65536,t=2,p=1$");
        verifyCall.Password.Should().Be(Password);

        var eventRow = await context.AuthEvents.AsNoTracking().SingleAsync(cancellationToken);
        eventRow.EventType.Should().Be(AuthEventTypes.LoginFailure);
        eventRow.UserId.Should().BeNull();
        eventRow.UsernameAttempted.Should().Be("ghost");
        eventRow.IpAddress.Should().Be("203.0.113.7");
        eventRow.UserAgent.Should().Be("auth-service-test");
        eventRow.OccurredAt.Should().Be(Start);
        eventRow.MetadataJson.Should().Contain(AuthService.UnknownUserReason);

        (await context.Users.CountAsync(cancellationToken)).Should().Be(0);
        (await context.UserSessions.CountAsync(cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Locked_user_is_rejected_before_any_password_verification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var hasher = new RecordingPasswordHasher();
        var lockedUntil = Start.AddMinutes(15);
        var user = await SeedUserAsync(
            hasher,
            lockedUntil: lockedUntil,
            failedLoginCount: 2,
            lastFailedLoginAt: Start.AddMinutes(-1),
            cancellationToken);

        await using var context = fixture.CreateContext();
        var service = CreateService(context, hasher, clock);

        var result = await service.LoginAsync(
            "smith",
            Password,
            "203.0.113.7",
            "auth-service-test",
            cancellationToken);

        result.Should().BeOfType<LoginResult.AccountLocked>()
            .Which.LockedUntil.Should().Be(lockedUntil);
        hasher.VerifyCalls.Should().BeEmpty();

        await using var verification = fixture.CreateContext();
        var stored = await verification.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(2);
        stored.LastFailedLoginAt.Should().Be(Start.AddMinutes(-1));
        stored.LockedUntil.Should().Be(lockedUntil);

        (await verification.UserSessions.CountAsync(cancellationToken)).Should().Be(0);
        var eventRow = await verification.AuthEvents.AsNoTracking().SingleAsync(cancellationToken);
        eventRow.EventType.Should().Be(AuthEventTypes.LoginFailure);
        eventRow.UserId.Should().Be(user.Id);
        eventRow.MetadataJson.Should().Contain(AuthService.AccountLockedReason);
    }

    [Fact]
    public async Task Fifth_consecutive_failure_requests_the_progressive_delay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var hasher = new RecordingPasswordHasher();
        var delayer = new RecordingLoginAttemptDelayer();
        var user = await SeedUserAsync(
            hasher,
            failedLoginCount: 4,
            cancellationToken: cancellationToken);

        await using var context = fixture.CreateContext();
        var service = CreateService(context, hasher, clock, delayer);

        var result = await service.LoginAsync(
            "smith",
            "wrong password entirely",
            "203.0.113.7",
            "auth-service-test",
            cancellationToken);

        result.Should().BeOfType<LoginResult.InvalidCredentials>();
        delayer.Waits.Should().Equal(TimeSpan.FromMilliseconds(250));

        await using var verification = fixture.CreateContext();
        var stored = await verification.Users
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == user.Id, cancellationToken);
        stored.FailedLoginCount.Should().Be(5);
        stored.LastFailedLoginAt.Should().Be(Start);
    }

    [Fact]
    public async Task Throttled_login_returns_the_exact_remaining_window()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var throttle = new LoginIpThrottle(
            clock,
            Options.Create(new LoginThrottleOptions
            {
                MaxFailuresPerWindow = 1,
                WindowSeconds = 60,
            }));

        await using var context = fixture.CreateContext();
        var service = CreateService(context, new RecordingPasswordHasher(), clock, throttle: throttle);

        var first = await service.LoginAsync(
            "ghost",
            Password,
            "203.0.113.7",
            "auth-service-test",
            cancellationToken);
        first.Should().BeOfType<LoginResult.InvalidCredentials>();

        var blocked = await service.LoginAsync(
            "ghost",
            Password,
            "203.0.113.7",
            "auth-service-test",
            cancellationToken);
        blocked.Should().BeOfType<LoginResult.RateLimited>()
            .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(10));

        var later = await service.LoginAsync(
            "ghost",
            Password,
            "203.0.113.7",
            "auth-service-test",
            cancellationToken);
        later.Should().BeOfType<LoginResult.RateLimited>()
            .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public async Task Logout_revokes_the_session_with_the_logout_reason_idempotently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync(new RecordingPasswordHasher(), cancellationToken: cancellationToken);

        long sessionId;
        await using (var context = fixture.CreateContext())
        {
            var (session, _) = await new SessionService(context, clock)
                .CreateAsync(user.Id, "127.0.0.1", "auth-service-test", cancellationToken);
            sessionId = session.Id;

            var service = CreateService(context, new RecordingPasswordHasher(), clock);
            (await service.LogoutAsync(sessionId, cancellationToken)).Should().BeTrue();
        }

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == sessionId, cancellationToken);
            stored.RevokedAt.Should().Be(Start);
            stored.RevokedReason.Should().Be(AuthService.LogoutRevocationReason);

            // A second logout keeps the first revocation untouched.
            clock.Advance(TimeSpan.FromHours(1));
            var service = CreateService(context, new RecordingPasswordHasher(), clock);
            (await service.LogoutAsync(sessionId, cancellationToken)).Should().BeTrue();
        }

        await using var verification = fixture.CreateContext();
        var final = await verification.UserSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == sessionId, cancellationToken);
        final.RevokedAt.Should().Be(Start);
        final.RevokedReason.Should().Be(AuthService.LogoutRevocationReason);
    }

    private static AuthService CreateService(
        AppDbContext context,
        IPasswordHasher hasher,
        TestTimeProvider clock,
        ILoginAttemptDelayer? delayer = null,
        ILoginIpThrottle? throttle = null,
        ILogger<AuthService>? logger = null) =>
        new(
            context,
            hasher,
            new SessionService(context, clock),
            new AuthEventWriter(context, clock),
            new LockoutLoginAttemptPolicy(),
            throttle ?? new LoginIpThrottle(clock, Options.Create(new LoginThrottleOptions())),
            delayer ?? new RecordingLoginAttemptDelayer(),
            new RecordingLiveSyncPublisher(),
            clock,
            logger ?? NullLogger<AuthService>.Instance);

    private async Task<User> SeedUserAsync(
        IPasswordHasher hasher,
        DateTimeOffset? lockedUntil = null,
        int failedLoginCount = 0,
        DateTimeOffset? lastFailedLoginAt = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = "smith",
            NormalizedUsername = "smith",
            PasswordHash = hasher.Hash(Password),
            Role = UserRole.Admin,
            IsActive = true,
            LockedUntil = lockedUntil,
            FailedLoginCount = failedLoginCount,
            LastFailedLoginAt = lastFailedLoginAt,
            CreatedAt = Start,
            UpdatedAt = Start,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private sealed class RecordingPasswordHasher : IPasswordHasher
    {
        private readonly Argon2idPasswordHasher _inner = new();

        public List<(string Hash, string Password)> VerifyCalls { get; } = [];

        public string Hash(string password) => _inner.Hash(password);

        public bool Verify(string hash, string password)
        {
            VerifyCalls.Add((hash, password));
            return _inner.Verify(hash, password);
        }

        public bool NeedsRehash(string hash) => _inner.NeedsRehash(hash);
    }
}
