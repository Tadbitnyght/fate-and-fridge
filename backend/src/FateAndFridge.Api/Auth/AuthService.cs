using System.Data;
using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// Too many recent attempts from this source address; the caller answers
    /// <c>429 RATE_LIMITED</c> with <see cref="RetryAfter"/> in the
    /// <c>Retry-After</c> header.
    /// </summary>
    public sealed record RateLimited(TimeSpan RetryAfter) : LoginResult;
}

/// <summary>Outcome of a self-service password change (docs/API.md §5).</summary>
public abstract record ChangePasswordResult
{
    private ChangePasswordResult()
    {
    }

    /// <summary>Password updated; the caller sets the cookie from <see cref="RotatedToken"/>.</summary>
    public sealed record Success(string RotatedToken, DateTimeOffset CookieExpiresAt) : ChangePasswordResult;

    /// <summary>Current password did not verify; generic 401.</summary>
    public sealed record InvalidCredentials : ChangePasswordResult;

    /// <summary>New password failed validation; 400 with field errors.</summary>
    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : ChangePasswordResult;

    /// <summary>User or session is no longer valid; 401 AUTH_REQUIRED.</summary>
    public sealed record UserUnavailable : ChangePasswordResult;
}

/// <summary>Outcome of an Admin password reset (docs/API.md §5).</summary>
public abstract record ResetPasswordResult
{
    private ResetPasswordResult()
    {
    }

    /// <summary>Temporary password returned exactly once; never persisted in plaintext.</summary>
    public sealed record Success(string TemporaryPassword) : ResetPasswordResult;

    /// <summary>The target user id does not exist.</summary>
    public sealed record UserNotFound : ResetPasswordResult;
}

/// <summary>
/// Credential check, session issue/revocation, and auth-event bookkeeping
/// (docs/SECURITY.md §2/§3, docs/DATABASE.md §2). Failures are generic
/// unknown usernames still pay one Argon2id verification so response timing
/// does not disclose account existence.
/// </summary>
/// <remarks>
/// Per-account lockout bookkeeping is delegated to
/// <see cref="LockoutLoginAttemptPolicy"/>. Failures are applied under an
/// atomic per-user row lock (<c>SELECT... FOR UPDATE</c> plus a single
/// counter write) so parallel attempts cannot lose increments. Per-IP limiting
/// reserves a slot through <see cref="ILoginIpThrottle"/> before verification.
/// </remarks>
public sealed class AuthService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    SessionService sessionService,
    IAuthEventWriter authEventWriter,
    ILoginAttemptPolicy loginAttemptPolicy,
    ILoginIpThrottle loginIpThrottle,
    ILoginAttemptDelayer loginAttemptDelayer,
    ILiveSyncPublisher liveSync,
    IAuditService auditService,
    TimeProvider timeProvider,
    ILogger<AuthService> logger)
{
    /// <summary><c>user_sessions.revoked_reason</c> written by <see cref="LogoutAsync"/>.</summary>
    public const string LogoutRevocationReason = "logout";

    /// <summary><c>user_sessions.revoked_reason</c> for a self-service password change.</summary>
    public const string PasswordChangeRevocationReason = "password_change";

    /// <summary><c>user_sessions.revoked_reason</c> for an Admin password reset.</summary>
    public const string PasswordResetRevocationReason = "password_reset";

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

        // Atomic per-IP reservation before any verification or database work
        // parallel attempts cannot slip past the threshold with a check-then
        // record race. Over-threshold attempts are rejected immediately and
        // deliberately write no auth event, only a warning log, so an abuse
        // burst cannot amplify database writes.
        var throttleDecision = loginIpThrottle.Reserve(ipAddress);
        if (!throttleDecision.Allowed)
        {
            logger.LogWarning(
                "Login attempt from {IpAddress} was rate limited; retry after {RetryAfterSeconds} seconds.",
                ipAddress ?? "(unknown)",
                Math.Max(1, (int)Math.Ceiling(throttleDecision.RetryAfter.TotalSeconds)));

            return new LoginResult.RateLimited(throttleDecision.RetryAfter);
        }

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

        if (!passwordHasher.Verify(user.PasswordHash, password))
        {
            var outcome = await RecordFailureAtomicallyAsync(user.Id, now, cancellationToken);

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
                        LockedUntilMetadata(outcome.State.LockedUntil)),
                    cancellationToken);
            }

            await ApplyDelayAsync(outcome.DelayAfterFailure, cancellationToken);
            return new LoginResult.InvalidCredentials();
        }

        loginAttemptPolicy.RecordSuccess(attemptState, now).ApplyTo(user);
        loginIpThrottle.RecordSuccess(ipAddress);

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

        // Mirror: the audit stream records the successful login without
        // any credential or session token (docs/PRODUCT_SPEC.md §43).
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.AuthLoginSucceeded,
                AuditEntityTypes.Session,
                EntityId: session.Id.ToString(CultureInfo.InvariantCulture),
                IpAddress: ipAddress,
                ActorUserId: user.Id,
                Metadata: new Dictionary<string, object?> { ["username"] = normalizedUsername }),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new LoginResult.Success(user, session, rawToken);
    }

    /// <summary>
    /// Revokes one session server-side and notifies that session's live-sync
    /// connections. The caller deletes the cookie.
    /// Returns false when the session id does not exist. The revocation and the
    /// <c>auth.logout</c> audit row commit atomically; an already-revoked
    /// session is a no-op and writes no audit row.
    /// </summary>
    public async Task<bool> LogoutAsync(
        long sessionId,
        long? actorUserId = null,
        string? username = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var alreadyRevoked = await dbContext.UserSessions
            .AsNoTracking()
            .AnyAsync(
                candidate => candidate.Id == sessionId && candidate.RevokedAt != null,
                cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var revoked = await sessionService.RevokeAsync(sessionId, LogoutRevocationReason, cancellationToken);
        if (!revoked)
        {
            return false;
        }

        if (!alreadyRevoked)
        {
            await auditService.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.AuthLogout,
                    AuditEntityTypes.Session,
                    EntityId: sessionId.ToString(CultureInfo.InvariantCulture),
                    IpAddress: ipAddress,
                    ActorUserId: actorUserId,
                    Metadata: username is null
                        ? null
                        : new Dictionary<string, object?> { ["username"] = username }),
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await liveSync.PublishSessionRevokedAsync(sessionId, cancellationToken);
        return true;
    }

    /// <summary>
    /// Changes the signed-in user's password (docs/API.md §5): verifies the
    /// current password, applies the 12–128 character policy, rejects an
    /// unchanged password, rotates the current session, revokes every other
    /// session, clears <c>must_change_password</c>, and writes a
    /// <c>password_change</c> event.
    /// </summary>
    public async Task<ChangePasswordResult> ChangePasswordAsync(
        long userId,
        long sessionId,
        string currentPassword,
        string newPassword,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);

        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == userId,
            cancellationToken);

        if (user is null || !user.IsActive)
        {
            return new ChangePasswordResult.UserUnavailable();
        }

        if (!passwordHasher.Verify(user.PasswordHash, currentPassword))
        {
            return new ChangePasswordResult.InvalidCredentials();
        }

        if (!PasswordPolicy.IsValid(newPassword))
        {
            return NewPasswordValidation(
                $"The new password must be between {PasswordPolicy.MinLength} and {PasswordPolicy.MaxLength} characters.");
        }

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            return NewPasswordValidation("The new password must be different from the current password.");
        }

        // Rotate, persist, revoke, and audit as one transaction: a failure must
        // not leave a rotated session with an unchanged password, or a changed
        // password with live sibling sessions.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Rotate before mutating the password: when the session can no longer be
        // rotated the request fails as unauthenticated without a partial change.
        var rotatedToken = await sessionService.RotateAsync(sessionId, cancellationToken);
        if (rotatedToken is null)
        {
            return new ChangePasswordResult.UserUnavailable();
        }

        var now = timeProvider.GetUtcNow();
        user.PasswordHash = passwordHasher.Hash(newPassword);
        user.MustChangePassword = false;
        user.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var revokedOtherSessions = await sessionService.RevokeOthersForUserAsync(
            userId,
            sessionId,
            PasswordChangeRevocationReason,
            cancellationToken);

        await authEventWriter.WriteAsync(
            new AuthEventEntry(
                AuthEventTypes.PasswordChange,
                user.Id,
                user.Username,
                ipAddress,
                userAgent,
                new Dictionary<string, object?> { ["revoked_other_sessions"] = revokedOtherSessions }),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // Only after the commit: notify the revoked sibling sessions' live-sync
        // connections and abort them (the rotating current session is excluded
        // via exceptSessionId).
        await liveSync.PublishSessionRevokedForUserAsync(userId, sessionId, cancellationToken);

        // The cookie expiry is a client-side hint; the database idle window
        // (refreshed by the rotation) remains authoritative.
        return new ChangePasswordResult.Success(rotatedToken, now + SessionService.IdleTimeout);
    }

    /// <summary>
    /// Admin password reset (docs/API.md §5): issues a one-time temporary
    /// password, sets <c>must_change_password</c>, clears all lockout state so
    /// the reset unblocks a locked account, revokes every session of the
    /// target, and writes a <c>password_reset</c> event naming the acting user.
    /// Inactive targets are reset as well; their login stays blocked until the
    /// user is re-enabled.
    /// </summary>
    public async Task<ResetPasswordResult> ResetPasswordAsync(
        long targetUserId,
        long? actorUserId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.SingleOrDefaultAsync(
            candidate => candidate.Id == targetUserId,
            cancellationToken);

        if (user is null)
        {
            return new ResetPasswordResult.UserNotFound();
        }

        // The new credential, the revoked sessions, and the audit event are one
        // transaction so a failure cannot leave a reset password with live
        // sessions (or vice versa).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var temporaryPassword = TemporaryPasswordGenerator.Generate();
        var now = timeProvider.GetUtcNow();
        user.PasswordHash = passwordHasher.Hash(temporaryPassword);
        user.MustChangePassword = true;

        // An admin reset is the only M1 flow that can unblock a locked account.
        user.FailedLoginCount = 0;
        user.LastFailedLoginAt = null;
        user.LockedUntil = null;
        user.LockoutCount = 0;
        user.LockoutWindowStartedAt = null;

        user.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        await sessionService.RevokeAllForUserAsync(user.Id, PasswordResetRevocationReason, cancellationToken);

        await authEventWriter.WriteAsync(
            new AuthEventEntry(
                AuthEventTypes.PasswordReset,
                user.Id,
                user.Username,
                ipAddress,
                userAgent,
                new Dictionary<string, object?> { ["actor_user_id"] = actorUserId }),
            cancellationToken);

        // An Admin reset is a user-lifecycle audit event. Only the
        // public fields and the fact that a temporary password was issued are
        // recorded — never the credential itself (docs/SECURITY.md §17).
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.UserPasswordReset,
                AuditEntityTypes.User,
                EntityId: user.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["id"] = user.Id,
                    ["username"] = user.Username,
                    ["must_change_password"] = user.MustChangePassword,
                }),
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: new Dictionary<string, object?> { ["temporary_password_issued"] = true }),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // Only after the commit: tell every revoked session's live-sync
        // connections and abort them.
        await liveSync.PublishSessionRevokedForUserAsync(user.Id, exceptSessionId: null, cancellationToken);

        return new ResetPasswordResult.Success(temporaryPassword);
    }

    /// <summary>
    /// Applies one credential failure under a per-user row lock so parallel
    /// attempts cannot lose increments (docs/SECURITY.md §3). The state is read
    /// with <c>SELECT... FOR UPDATE</c>, the pure policy decides the outcome
    /// and the counters are written back in the same transaction; concurrent
    /// claimers serialize on the row, so the 10th parallel failure
    /// deterministically engages the lock.
    /// </summary>
    private async Task<LoginAttemptOutcome> RecordFailureAtomicallyAsync(
        long userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var state = await LoadLockoutStateForUpdateAsync(userId, cancellationToken);
        var outcome = loginAttemptPolicy.RecordFailure(state, now);

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE users
            SET failed_login_count = {outcome.State.FailedLoginCount},
                last_failed_login_at = {outcome.State.LastFailedLoginAt},
                lockout_count = {outcome.State.LockoutCount},
                lockout_window_started_at = {outcome.State.LockoutWindowStartedAt},
                locked_until = {outcome.State.LockedUntil},
                updated_at = {now}
            WHERE id = {userId}
            """,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    /// <summary>
    /// Reads the lockout counters of one user while holding the row lock for
    /// the enclosing transaction.
    /// </summary>
    private async Task<LoginAttemptState> LoadLockoutStateForUpdateAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            """
            SELECT failed_login_count, last_failed_login_at, lockout_count, lockout_window_started_at, locked_until
            FROM users
            WHERE id = @userId
            FOR UPDATE
            """;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "userId";
        parameter.Value = userId;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"User {userId} disappeared while recording a login failure.");
        }

        return new LoginAttemptState(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
    }

    private async Task WriteFailureAsync(
        long? userId,
        string normalizedUsername,
        string? ipAddress,
        string? userAgent,
        string reason,
        CancellationToken cancellationToken)
    {
        await authEventWriter.WriteAsync(
            new AuthEventEntry(
                AuthEventTypes.LoginFailure,
                userId,
                normalizedUsername,
                ipAddress,
                userAgent,
                new Dictionary<string, object?> { ["reason"] = reason }),
            cancellationToken);

        // Mirror: the attempted username (never the password), the
        // outcome category, and the IP; only the outcome category is stored.
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.AuthLoginFailed,
                AuditEntityTypes.Auth,
                IpAddress: ipAddress,
                ActorUserId: userId,
                Metadata: new Dictionary<string, object?>
                {
                    ["username"] = normalizedUsername,
                    ["reason"] = reason,
                }),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyDictionary<string, object?>? LockedUntilMetadata(DateTimeOffset? lockedUntil) =>
        lockedUntil is null
            ? null
            : new Dictionary<string, object?> { ["locked_until"] = lockedUntil };

    private static ChangePasswordResult.ValidationFailed NewPasswordValidation(string message) =>
        new(new Dictionary<string, string[]> { ["new_password"] = [message] });

    private async Task ApplyDelayAsync(TimeSpan? delay, CancellationToken cancellationToken)
    {
        if (delay is { } value && value > TimeSpan.Zero)
        {
            await loginAttemptDelayer.DelayAsync(value, cancellationToken);
        }
    }
}
