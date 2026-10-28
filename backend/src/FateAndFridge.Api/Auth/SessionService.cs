using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Auth;

/// <summary>A valid (unrevoked, unexpired) session and its owning user.</summary>
public sealed record ResolvedSession(UserSession Session, User User);

/// <summary>
/// Database-backed opaque session lifecycle (docs/SECURITY.md §2
/// docs/DATABASE.md §2): 30-day idle window, 90-day absolute cap, rotation
/// and explicit revocation. Only a SHA-256 hash of the token is stored; the raw
/// token exists only in transit and is never logged.
/// </summary>
public sealed class SessionService(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Sliding idle window.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromDays(30);

    /// <summary>Hard session lifetime cap.</summary>
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromDays(90);

    /// <summary>Minimum gap between persisted activity touches.</summary>
    public static readonly TimeSpan TouchThrottle = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Creates a session and returns the raw token for the caller to place in
    /// the session cookie. The database row stores only
    /// <c>SHA-256(rawToken)</c>.
    /// </summary>
    public async Task<(UserSession Session, string RawToken)> CreateAsync(
        long userId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var rawToken = SessionTokenHasher.CreateRawToken();
        var session = new UserSession
        {
            UserId = userId,
            TokenHash = SessionTokenHasher.Hash(rawToken),
            CreatedAt = now,
            LastSeenAt = now,
            IdleExpiresAt = now + IdleTimeout,
            AbsoluteExpiresAt = now + AbsoluteTimeout,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };

        dbContext.UserSessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        return (session, rawToken);
    }

    /// <summary>
    /// Resolves a raw token to its session and user. Revoked, idle-expired
    /// absolute-expired, and inactive-user sessions resolve to null. Activity
    /// refreshes <c>last_seen_at</c>/<c>idle_expires_at</c> at most once per
    /// <see cref="TouchThrottle"/> so request traffic does not amplify writes.
    /// Once the idle window has reached the absolute cap there is nothing left
    /// to refresh, so no further activity writes occur.
    /// </summary>
    public async Task<ResolvedSession?> ResolveAsync(
        string? rawToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var tokenHash = SessionTokenHasher.Hash(rawToken);
        var match = await (
                from session in dbContext.UserSessions
                join user in dbContext.Users on session.UserId equals user.Id
                where session.TokenHash == tokenHash
                select new { Session = session, User = user })
            .SingleOrDefaultAsync(cancellationToken);

        if (match is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (match.Session.RevokedAt is not null
            || now >= match.Session.IdleExpiresAt
            || now >= match.Session.AbsoluteExpiresAt
            || !match.User.IsActive)
        {
            return null;
        }

        // A clamped idle window cannot be extended, so skip the write entirely.
        if (match.Session.IdleExpiresAt < match.Session.AbsoluteExpiresAt
            && now - match.Session.LastSeenAt >= TouchThrottle)
        {
            match.Session.LastSeenAt = now;
            match.Session.IdleExpiresAt = RefreshIdleExpiry(now, match.Session.AbsoluteExpiresAt);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ResolvedSession(match.Session, match.User);
    }

    /// <summary>
    /// Replaces the session token with a fresh one. The old token stops
    /// resolving immediately; the 90-day absolute cap is preserved and the idle
    /// window restarts. Returns null when the session no longer exists or can no
    /// longer be rotated (revoked or expired).
    /// </summary>
    public async Task<string?> RotateAsync(
        long sessionId,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.UserSessions
            .SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (session.RevokedAt is not null
            || now >= session.IdleExpiresAt
            || now >= session.AbsoluteExpiresAt)
        {
            return null;
        }

        var rawToken = SessionTokenHasher.CreateRawToken();
        session.TokenHash = SessionTokenHasher.Hash(rawToken);
        session.LastSeenAt = now;
        session.IdleExpiresAt = RefreshIdleExpiry(now, session.AbsoluteExpiresAt);
        await dbContext.SaveChangesAsync(cancellationToken);

        return rawToken;
    }

    /// <summary>
    /// Revokes one session. Idempotent: revoking an already-revoked session
    /// keeps the original revocation timestamp and reason. Returns false when
    /// the session does not exist.
    /// </summary>
    public async Task<bool> RevokeAsync(
        long sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var session = await dbContext.UserSessions
            .SingleOrDefaultAsync(candidate => candidate.Id == sessionId, cancellationToken);

        if (session is null)
        {
            return false;
        }

        if (session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            session.RevokedReason = reason;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Revokes every active session of a user (password reset, disable
    /// privilege change) and returns the number of sessions revoked.
    /// </summary>
    public async Task<int> RevokeAllForUserAsync(
        long userId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = timeProvider.GetUtcNow();
        return await dbContext.UserSessions
            .Where(session => session.UserId == userId && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.RevokedReason, reason),
                cancellationToken);
    }

    /// <summary>
    /// Revokes every active session of a user except <paramref name="exceptSessionId"/>
    /// (used by password change so the rotating session stays valid) and returns
    /// the number of sessions revoked. reuses this for privilege changes.
    /// </summary>
    public async Task<int> RevokeOthersForUserAsync(
        long userId,
        long exceptSessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var now = timeProvider.GetUtcNow();
        return await dbContext.UserSessions
            .Where(session => session.UserId == userId
                && session.Id != exceptSessionId
                && session.RevokedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(session => session.RevokedAt, now)
                    .SetProperty(session => session.RevokedReason, reason),
                cancellationToken);
    }

    private static DateTimeOffset RefreshIdleExpiry(DateTimeOffset now, DateTimeOffset absoluteExpiresAt)
    {
        var idleExpiresAt = now + IdleTimeout;
        return idleExpiresAt < absoluteExpiresAt ? idleExpiresAt : absoluteExpiresAt;
    }
}
