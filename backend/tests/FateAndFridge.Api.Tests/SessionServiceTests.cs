using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of session creation, resolution, expiry
/// rotation, revocation, and logging hygiene (docs/SECURITY.md §2
/// docs/DATABASE.md §2). Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class SessionServiceTests(SetupFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Create_stores_only_the_sha256_hash_of_the_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);

        var (session, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        await using var context = fixture.CreateContext();
        var stored = await context.UserSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id, cancellationToken);

        stored.TokenHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
        stored.CreatedAt.Should().Be(Start);
        stored.LastSeenAt.Should().Be(Start);
        stored.IdleExpiresAt.Should().Be(Start.Add(SessionService.IdleTimeout));
        stored.AbsoluteExpiresAt.Should().Be(Start.Add(SessionService.AbsoluteTimeout));
        stored.UserId.Should().Be(user.Id);
        stored.IpAddress.Should().Be("127.0.0.1");
        stored.UserAgent.Should().Be("test-agent");
        stored.RevokedAt.Should().BeNull();
        stored.RevokedReason.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_returns_the_session_and_user_for_a_valid_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync(rawToken, cancellationToken);

        resolved.Should().NotBeNull();
        resolved!.Session.Id.Should().Be(session.Id);
        resolved.User.Id.Should().Be(user.Id);
        resolved.User.Username.Should().Be("smith");
    }

    [Fact]
    public async Task Resolve_returns_null_for_an_unknown_token()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);

        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync("not-a-real-token", cancellationToken);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_rejects_a_revoked_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        await using (var revokeContext = fixture.CreateContext())
        {
            var revoked = await new SessionService(revokeContext, clock)
                .RevokeAsync(session.Id, "password_change", cancellationToken);
            revoked.Should().BeTrue();
        }

        await using (var context = fixture.CreateContext())
        {
            var resolved = await new SessionService(context, clock)
                .ResolveAsync(rawToken, cancellationToken);
            resolved.Should().BeNull();
        }

        await using var verification = fixture.CreateContext();
        var stored = await verification.UserSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id, cancellationToken);
        stored.RevokedAt.Should().Be(Start);
        stored.RevokedReason.Should().Be("password_change");
    }

    [Fact]
    public async Task Resolve_rejects_a_session_at_the_idle_deadline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (_, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        clock.Advance(SessionService.IdleTimeout);

        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync(rawToken, cancellationToken);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_rejects_a_session_past_its_absolute_cap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (_, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        clock.Advance(SessionService.AbsoluteTimeout);

        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync(rawToken, cancellationToken);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_rejects_an_absolute_expired_session_whose_idle_window_is_still_fresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        // Idle expiry is normally clamped to the absolute cap, so reach this
        // state directly: a fresh idle window under an already-passed cap.
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE user_sessions
                SET last_seen_at = {clock.UtcNow},
                    idle_expires_at = {clock.UtcNow.Add(SessionService.IdleTimeout)},
                    absolute_expires_at = {clock.UtcNow.AddSeconds(-1)}
                WHERE id = {session.Id}
                """,
                cancellationToken);
        }

        await using var resolveContext = fixture.CreateContext();
        var resolved = await new SessionService(resolveContext, clock)
            .ResolveAsync(rawToken, cancellationToken);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Resolve_touches_last_seen_at_at_most_once_per_throttle_interval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        // Inside the throttle window the row is not written.
        clock.Advance(TimeSpan.FromSeconds(30));
        await ResolveOnceAsync(rawToken, clock, cancellationToken);
        await AssertLastSeenAsync(session.Id, Start, cancellationToken);

        // Crossing the throttle refreshes last_seen_at and the idle expiry.
        clock.Advance(TimeSpan.FromSeconds(31));
        await ResolveOnceAsync(rawToken, clock, cancellationToken);
        await AssertLastSeenAsync(session.Id, clock.UtcNow, cancellationToken);
        await AssertIdleExpiresAtAsync(
            session.Id,
            clock.UtcNow.Add(SessionService.IdleTimeout),
            cancellationToken);

        // The next resolve inside the new window does not write again.
        clock.Advance(TimeSpan.FromSeconds(30));
        await ResolveOnceAsync(rawToken, clock, cancellationToken);
        await AssertLastSeenAsync(
            session.Id,
            clock.UtcNow.AddSeconds(-30),
            cancellationToken);
    }

    [Fact]
    public async Task Rotate_returns_a_new_token_and_invalidates_the_old_one()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, oldToken) = await CreateSessionAsync(clock, user, cancellationToken);

        clock.Advance(TimeSpan.FromDays(10));

        string? newToken;
        await using (var context = fixture.CreateContext())
        {
            newToken = await new SessionService(context, clock)
                .RotateAsync(session.Id, cancellationToken);
        }

        newToken.Should().NotBeNull();
        newToken.Should().NotBe(oldToken);

        await using (var context = fixture.CreateContext())
        {
            var service = new SessionService(context, clock);
            (await service.ResolveAsync(oldToken, cancellationToken)).Should().BeNull();
            (await service.ResolveAsync(newToken, cancellationToken)).Should().NotBeNull();
        }

        await using var verification = fixture.CreateContext();
        var stored = await verification.UserSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id, cancellationToken);
        stored.TokenHash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(newToken!)));
        stored.AbsoluteExpiresAt.Should().Be(Start.Add(SessionService.AbsoluteTimeout));
        stored.IdleExpiresAt.Should().Be(clock.UtcNow.Add(SessionService.IdleTimeout));
        stored.LastSeenAt.Should().Be(clock.UtcNow);
    }

    [Fact]
    public async Task Rotate_rejects_revoked_and_expired_sessions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (revoked, _) = await CreateSessionAsync(clock, user, cancellationToken);
        var (expired, _) = await CreateSessionAsync(clock, user, cancellationToken);

        await using (var context = fixture.CreateContext())
        {
            var service = new SessionService(context, clock);
            (await service.RevokeAsync(revoked.Id, "logout", cancellationToken)).Should().BeTrue();
        }

        clock.Advance(SessionService.AbsoluteTimeout);

        await using (var context = fixture.CreateContext())
        {
            var service = new SessionService(context, clock);
            (await service.RotateAsync(revoked.Id, cancellationToken)).Should().BeNull();
            (await service.RotateAsync(expired.Id, cancellationToken)).Should().BeNull();
        }
    }

    [Fact]
    public async Task Revoke_all_for_user_revokes_only_that_users_active_sessions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var alice = await SeedUserAsync("alice", cancellationToken);
        var bob = await SeedUserAsync("bob", cancellationToken);
        var (_, aliceFirstToken) = await CreateSessionAsync(clock, alice, cancellationToken);
        var (aliceSecond, _) = await CreateSessionAsync(clock, alice, cancellationToken);
        var (bobSession, bobToken) = await CreateSessionAsync(clock, bob, cancellationToken);

        int revokedCount;
        await using (var context = fixture.CreateContext())
        {
            revokedCount = await new SessionService(context, clock)
                .RevokeAllForUserAsync(alice.Id, "user_disabled", cancellationToken);
        }

        revokedCount.Should().Be(2);

        await using (var verification = fixture.CreateContext())
        {
            var aliceSessions = await verification.UserSessions
                .AsNoTracking()
                .Where(candidate => candidate.UserId == alice.Id)
                .ToListAsync(cancellationToken);
            aliceSessions.Should().HaveCount(2);
            aliceSessions.Should().OnlyContain(candidate =>
                candidate.RevokedAt == Start && candidate.RevokedReason == "user_disabled");

            var storedBob = await verification.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == bobSession.Id, cancellationToken);
            storedBob.RevokedAt.Should().BeNull();
            storedBob.RevokedReason.Should().BeNull();
        }

        await using (var context = fixture.CreateContext())
        {
            var service = new SessionService(context, clock);
            (await service.ResolveAsync(aliceFirstToken, cancellationToken)).Should().BeNull();
            (await service.ResolveAsync(bobToken, cancellationToken)).Should().NotBeNull();
        }

        // Revoking all again is a no-op for already-revoked sessions.
        await using (var context = fixture.CreateContext())
        {
            var secondRun = await new SessionService(context, clock)
                .RevokeAllForUserAsync(alice.Id, "user_disabled", cancellationToken);
            secondRun.Should().Be(0);
        }

        await using var finalVerification = fixture.CreateContext();
        var untouched = await finalVerification.UserSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == aliceSecond.Id, cancellationToken);
        untouched.RevokedAt.Should().Be(Start);
    }

    [Fact]
    public async Task Revoke_others_for_user_keeps_the_excepted_session_active()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var alice = await SeedUserAsync("alice", cancellationToken);
        var bob = await SeedUserAsync("bob", cancellationToken);
        var (aliceKept, aliceKeptToken) = await CreateSessionAsync(clock, alice, cancellationToken);
        var (aliceRevoked, aliceRevokedToken) = await CreateSessionAsync(clock, alice, cancellationToken);
        var (_, bobToken) = await CreateSessionAsync(clock, bob, cancellationToken);

        int revokedCount;
        await using (var context = fixture.CreateContext())
        {
            revokedCount = await new SessionService(context, clock)
                .RevokeOthersForUserAsync(
                    alice.Id,
                    aliceKept.Id,
                    "password_change",
                    cancellationToken);
        }

        revokedCount.Should().Be(1);

        await using (var verification = fixture.CreateContext())
        {
            var kept = await verification.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == aliceKept.Id, cancellationToken);
            kept.RevokedAt.Should().BeNull();

            var revoked = await verification.UserSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == aliceRevoked.Id, cancellationToken);
            revoked.RevokedAt.Should().Be(Start);
            revoked.RevokedReason.Should().Be("password_change");
        }

        await using (var context = fixture.CreateContext())
        {
            var service = new SessionService(context, clock);
            (await service.ResolveAsync(aliceKeptToken, cancellationToken)).Should().NotBeNull();
            (await service.ResolveAsync(aliceRevokedToken, cancellationToken)).Should().BeNull();
            (await service.ResolveAsync(bobToken, cancellationToken)).Should().NotBeNull();
        }

        // Idempotent: nothing left to revoke for the excepted session.
        await using (var context = fixture.CreateContext())
        {
            (await new SessionService(context, clock)
                    .RevokeOthersForUserAsync(alice.Id, aliceKept.Id, "password_change", cancellationToken))
                .Should().Be(0);
        }
    }

    [Fact]
    public async Task Session_lifecycle_never_logs_the_raw_token_or_its_hash()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var recorder = new RecordingLoggerProvider();
        using var factory = fixture.CreateFactory().WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(recorder);
            }));

        var user = await SeedUserAsync("smith", cancellationToken);
        string rawToken;
        await using (var context = fixture.CreateContext())
        {
            var created = await new SessionService(context, TimeProvider.System)
                .CreateAsync(user.Id, "127.0.0.1", "test-agent", cancellationToken);
            rawToken = created.RawToken;
        }

        // Resolve through the real HTTP pipeline as well: the cookie handler
        // must not log the token either.
        using (var client = factory.CreateClient())
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health"))
        {
            request.Headers.Add("Cookie", $"{SessionAuthenticationDefaults.CookieName}={rawToken}");
            await client.SendAsync(request, cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            await new SessionService(context, TimeProvider.System)
                .ResolveAsync(rawToken, cancellationToken);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        var secretRepresentations = TokenRepresentations(rawToken, hash).ToList();
        recorder.AllText.Should().NotContain(text =>
            secretRepresentations.Any(secret => text.Contains(secret, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Every representation a logger could plausibly emit for the token or its
    /// hash: the base64url token, standard base64/hex of the token bytes, and
    /// base64url/base64/hex of the hash bytes.
    /// </summary>
    private static IEnumerable<string> TokenRepresentations(string rawToken, byte[] hash)
    {
        var tokenBytes = Base64Url.DecodeFromChars(rawToken);
        yield return rawToken;
        yield return Convert.ToBase64String(tokenBytes);
        yield return Convert.ToHexString(tokenBytes);
        yield return Base64Url.EncodeToString(hash);
        yield return Convert.ToBase64String(hash);
        yield return Convert.ToHexString(hash);
    }

    [Fact]
    public async Task Resolve_rejects_a_session_for_an_inactive_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken, isActive: false);
        var (_, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync(rawToken, cancellationToken);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task Revoke_is_idempotent_and_preserves_the_first_revocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, _) = await CreateSessionAsync(clock, user, cancellationToken);

        await using (var context = fixture.CreateContext())
        {
            var revoked = await new SessionService(context, clock)
                .RevokeAsync(session.Id, "logout", cancellationToken);
            revoked.Should().BeTrue();
        }

        clock.Advance(TimeSpan.FromHours(1));

        await using (var context = fixture.CreateContext())
        {
            var service = new SessionService(context, clock);
            (await service.RevokeAsync(session.Id, "password_change", cancellationToken))
                .Should().BeTrue();
            (await service.RevokeAsync(999_999, "logout", cancellationToken)).Should().BeFalse();
        }

        await using var verification = fixture.CreateContext();
        var stored = await verification.UserSessions
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == session.Id, cancellationToken);
        stored.RevokedAt.Should().Be(Start);
        stored.RevokedReason.Should().Be("logout");
    }

    [Fact]
    public async Task Resolve_accepts_one_second_before_the_idle_deadline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (_, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        clock.Advance(SessionService.IdleTimeout - TimeSpan.FromSeconds(1));

        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync(rawToken, cancellationToken);

        resolved.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_accepts_one_second_before_the_absolute_cap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (_, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        // Keep the session alive until the idle window clamps to the cap.
        await KeepAliveUntilAsync(
            rawToken,
            clock,
            Start.Add(SessionService.AbsoluteTimeout - SessionService.IdleTimeout),
            cancellationToken);

        clock.Advance(SessionService.IdleTimeout - TimeSpan.FromSeconds(1));

        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync(rawToken, cancellationToken);

        resolved.Should().NotBeNull();
    }

    [Fact]
    public async Task Resolve_touches_at_exactly_the_throttle_interval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        clock.Advance(SessionService.TouchThrottle);
        await ResolveOnceAsync(rawToken, clock, cancellationToken);

        await AssertLastSeenAsync(session.Id, clock.UtcNow, cancellationToken);
        await AssertIdleExpiresAtAsync(
            session.Id,
            clock.UtcNow.Add(SessionService.IdleTimeout),
            cancellationToken);
    }

    [Fact]
    public async Task Resolve_does_not_write_once_the_idle_window_is_clamped_to_the_absolute_cap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new TestTimeProvider(Start);
        var user = await SeedUserAsync("smith", cancellationToken);
        var (session, rawToken) = await CreateSessionAsync(clock, user, cancellationToken);

        // Activity near the cap clamps idle == absolute.
        await KeepAliveUntilAsync(
            rawToken,
            clock,
            Start.Add(SessionService.AbsoluteTimeout - SessionService.IdleTimeout),
            cancellationToken);
        await AssertIdleExpiresAtAsync(
            session.Id,
            Start.Add(SessionService.AbsoluteTimeout),
            cancellationToken);
        var clampedLastSeenAt = clock.UtcNow;

        // The throttle is satisfied again, but a clamped idle window cannot be
        // extended, so no write may happen.
        clock.Advance(SessionService.TouchThrottle);
        await ResolveOnceAsync(rawToken, clock, cancellationToken);

        await AssertLastSeenAsync(session.Id, clampedLastSeenAt, cancellationToken);
    }

    private async Task<User> SeedUserAsync(
        string username,
        CancellationToken cancellationToken,
        bool isActive = true)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = username.ToLowerInvariant(),
            PasswordHash = "phc-placeholder",
            Role = UserRole.Admin,
            IsActive = isActive,
            CreatedAt = Start,
            UpdatedAt = Start,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task<(UserSession Session, string RawToken)> CreateSessionAsync(
        TestTimeProvider clock,
        User user,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await new SessionService(context, clock)
            .CreateAsync(user.Id, "127.0.0.1", "test-agent", cancellationToken);
    }

    /// <summary>
    /// Resolves the token while advancing the clock in half-idle steps so the
    /// session stays alive until <paramref name="target"/> is reached.
    /// </summary>
    private async Task KeepAliveUntilAsync(
        string rawToken,
        TestTimeProvider clock,
        DateTimeOffset target,
        CancellationToken cancellationToken)
    {
        var maxStep = SessionService.IdleTimeout / 2;
        while (clock.UtcNow < target)
        {
            var step = target - clock.UtcNow;
            clock.Advance(step > maxStep ? maxStep : step);
            await ResolveOnceAsync(rawToken, clock, cancellationToken);
        }
    }

    private async Task ResolveOnceAsync(
        string rawToken,
        TestTimeProvider clock,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var resolved = await new SessionService(context, clock)
            .ResolveAsync(rawToken, cancellationToken);
        resolved.Should().NotBeNull();
    }

    private async Task AssertLastSeenAsync(
        long sessionId,
        DateTimeOffset expected,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var lastSeenAt = await context.UserSessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == sessionId)
            .Select(candidate => candidate.LastSeenAt)
            .SingleAsync(cancellationToken);
        lastSeenAt.Should().Be(expected);
    }

    private async Task AssertIdleExpiresAtAsync(
        long sessionId,
        DateTimeOffset expected,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var idleExpiresAt = await context.UserSessions
            .AsNoTracking()
            .Where(candidate => candidate.Id == sessionId)
            .Select(candidate => candidate.IdleExpiresAt)
            .SingleAsync(cancellationToken);
        idleExpiresAt.Should().Be(expected);
    }
}
