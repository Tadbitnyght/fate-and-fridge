using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Auth;

/// <summary>Outcome of a login attempt (docs/API.md §5).</summary>
public abstract record LoginResult
{
    private LoginResult()
    {
    }

    /// <summary>Credentials verified; the caller sets the cookie from <see cref="RawToken"/>.</summary>
    public sealed record Success(User User, UserSession Session, string RawToken) : LoginResult;

    /// <summary>
    /// Generic rejection shared by unknown usernames, inactive users, and
    /// wrong passwords so login never reveals whether an account exists.
    /// </summary>
    public sealed record InvalidCredentials : LoginResult;

    /// <summary>An existing <c>locked_until</c> is still in the future.</summary>
    public sealed record AccountLocked(DateTimeOffset LockedUntil) : LoginResult;
}

/// <summary>
/// Credential check, session issue/revocation, and auth-event bookkeeping
/// (M1-07; docs/SECURITY.md §2/§3, docs/DATABASE.md §2). Failures are generic;
/// unknown usernames still pay one Argon2id verification so response timing
/// does not disclose account existence.
/// </summary>
/// <remarks>
/// Lockout thresholds, progressive delay, and per-IP rate limiting are part 2.
/// This class only honors an already-stored <c>locked_until</c> and delegates
/// counter bookkeeping to <see cref="ILoginAttemptPolicy"/>.
/// </remarks>
public sealed class AuthService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    SessionService sessionService,
    IAuthEventWriter authEventWriter,
    ILoginAttemptPolicy loginAttemptPolicy,
    TimeProvider timeProvider)
{
    /// <summary><c>user_sessions.revoked_reason</c> written by <see cref="LogoutAsync"/>.</summary>
    public const string LogoutRevocationReason = "logout";

    /// <summary>Auth-event metadata reason categories (never secret material).</summary>
    public const string UnknownUserReason = "unknown_user";

    public const string InactiveUserReason = "inactive_user";

    public const string AccountLockedReason = "account_locked";

    public const string InvalidPasswordReason = "invalid_password";

    /// <summary>
    /// PHC hash of an unguessable random password that matches no account. An
    /// unknown username still runs one Argon2id verification against it, so
    /// the unknown-user path costs the same as a wrong-password check; the
    /// boolean result is intentionally discarded.
    /// </summary>
    private const string DummyPasswordHash =
        "$argon2id$v=19$m=65536,t=2,p=1$BkbVX3u2ECSuBuxGT2+Rnw$fMER1PA9Y41ltuxC813QiWf235Yl8v9UZdFJS7vC5OU";

    /// <summary>
    /// Verifies credentials and, on success, creates a fresh opaque session
    /// (rotation on login) and resets the consecutive-failure counters. Always
    /// writes a <c>login_success</c> or <c>login_failure</c> auth event.
    /// </summary>
    public async Task<LoginResult> LoginAsync(
        string username,
        string password,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        var normalizedUsername = UsernameNormalizer.Normalize(username);
        var now = timeProvider.GetUtcNow();

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.NormalizedUsername == normalizedUsername,
            cancellationToken);

        if (user is null)
        {
            // Timing equalization: pay the verification cost for unknown users.
            passwordHasher.Verify(DummyPasswordHash, password);
            await WriteFailureAsync(
                userId: null,
                normalizedUsername,
                ipAddress,
                userAgent,
                UnknownUserReason,
                cancellationToken);
            return new LoginResult.InvalidCredentials();
        }

        if (!user.IsActive)
        {
            // Disabled users are rejected generically, before any lock check.
            passwordHasher.Verify(user.PasswordHash, password);
            await WriteFailureAsync(
                user.Id,
                normalizedUsername,
                ipAddress,
                userAgent,
                InactiveUserReason,
                cancellationToken);
            return new LoginResult.InvalidCredentials();
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > now)
        {
            await WriteFailureAsync(
                user.Id,
                normalizedUsername,
                ipAddress,
                userAgent,
                AccountLockedReason,
                cancellationToken);
            return new LoginResult.AccountLocked(lockedUntil);
        }

        var attemptState = LoginAttemptState.From(user);
        await ApplyDelayAsync(
            loginAttemptPolicy.GetVerificationDelay(attemptState, now),
            cancellationToken);

        if (!passwordHasher.Verify(user.PasswordHash, password))
        {
            var outcome = loginAttemptPolicy.RecordFailure(attemptState, now);
            outcome.State.ApplyTo(user);
            user.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);

            await WriteFailureAsync(
                user.Id,
                normalizedUsername,
                ipAddress,
                userAgent,
                InvalidPasswordReason,
                cancellationToken);

            if (outcome.LockEngaged)
            {
                await authEventWriter.WriteAsync(
                    new AuthEventEntry(
                        AuthEventTypes.AccountLocked,
                        user.Id,
                        normalizedUsername,
                        ipAddress,
                        userAgent,
                        LockedUntilMetadata(user.LockedUntil)),
                    cancellationToken);
            }

            await ApplyDelayAsync(outcome.DelayAfterFailure, cancellationToken);
            return new LoginResult.InvalidCredentials();
        }

        loginAttemptPolicy.RecordSuccess(attemptState, now).ApplyTo(user);

        // Rehash after a successful login when the stored hash uses older
        // parameters (docs/SECURITY.md §2). Plaintext stays in this frame.
        if (passwordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = passwordHasher.Hash(password);
        }

        user.LastLoginAt = now;
        user.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var (session, rawToken) = await sessionService.CreateAsync(
            user.Id,
            ipAddress,
            userAgent,
            cancellationToken);

        await authEventWriter.WriteAsync(
            new AuthEventEntry(
                AuthEventTypes.LoginSuccess,
                user.Id,
                normalizedUsername,
                ipAddress,
                userAgent),
            cancellationToken);

        return new LoginResult.Success(user, session, rawToken);
    }

    /// <summary>
    /// Revokes one session server-side. The caller deletes the cookie. Returns
    /// false when the session id does not exist.
    /// </summary>
    public Task<bool> LogoutAsync(long sessionId, CancellationToken cancellationToken = default) =>
        sessionService.RevokeAsync(sessionId, LogoutRevocationReason, cancellationToken);

    private Task WriteFailureAsync(
        long? userId,
        string normalizedUsername,
        string? ipAddress,
        string? userAgent,
        string reason,
        CancellationToken cancellationToken) =>
        authEventWriter.WriteAsync(
            new AuthEventEntry(
                AuthEventTypes.LoginFailure,
                userId,
                normalizedUsername,
                ipAddress,
                userAgent,
                new Dictionary<string, object?> { ["reason"] = reason }),
            cancellationToken);

    private static IReadOnlyDictionary<string, object?>? LockedUntilMetadata(DateTimeOffset? lockedUntil) =>
        lockedUntil is null
            ? null
            : new Dictionary<string, object?> { ["locked_until"] = lockedUntil };

    private async Task ApplyDelayAsync(TimeSpan? delay, CancellationToken cancellationToken)
    {
        if (delay is { } value && value > TimeSpan.Zero)
        {
            await Task.Delay(value, timeProvider, cancellationToken);
        }
    }
}
