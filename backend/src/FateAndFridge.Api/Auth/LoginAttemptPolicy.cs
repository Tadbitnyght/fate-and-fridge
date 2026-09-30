using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Lockout counter state the login path passes to <see cref="ILoginAttemptPolicy"/>.
/// Mirrors the <c>users</c> lockout columns (docs/DATABASE.md §2) so the policy
/// stays free of database and HTTP concerns and is directly unit-testable.
/// </summary>
public sealed record LoginAttemptState(
    int FailedLoginCount,
    DateTimeOffset? LastFailedLoginAt,
    int LockoutCount,
    DateTimeOffset? LockoutWindowStartedAt,
    DateTimeOffset? LockedUntil)
{
    /// <summary>Reads the lockout counters of <paramref name="user"/>.</summary>
    public static LoginAttemptState From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new LoginAttemptState(
            user.FailedLoginCount,
            user.LastFailedLoginAt,
            user.LockoutCount,
            user.LockoutWindowStartedAt,
            user.LockedUntil);
    }

    /// <summary>Writes the counters back onto <paramref name="user"/>; the caller saves.</summary>
    public void ApplyTo(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        user.FailedLoginCount = FailedLoginCount;
        user.LastFailedLoginAt = LastFailedLoginAt;
        user.LockoutCount = LockoutCount;
        user.LockoutWindowStartedAt = LockoutWindowStartedAt;
        user.LockedUntil = LockedUntil;
    }
}

/// <summary>
/// Result of recording a credential failure: the updated counter state plus
/// optional part-2 decisions (progressive delay, lock engagement).
/// </summary>
/// <param name="State">Counters to apply to the user row.</param>
/// <param name="LockEngaged">True when this failure triggered a lock; the login path writes an <c>account_locked</c> event.</param>
/// <param name="DelayAfterFailure">Optional progressive delay applied before the response (part 2).</param>
public sealed record LoginAttemptOutcome(
    LoginAttemptState State,
    bool LockEngaged,
    TimeSpan? DelayAfterFailure);

/// <summary>
/// Login-attempt policy seam (M1-07). Part 1 injects
/// <see cref="DefaultLoginAttemptPolicy"/>, which maintains the
/// consecutive-failure counter only. Part 2 replaces the registration with the
/// configured thresholds, progressive delay, and lockout windows from
/// docs/SECURITY.md §3 without changing <see cref="AuthService"/>.
/// </summary>
public interface ILoginAttemptPolicy
{
    /// <summary>
    /// Delay applied before the Argon2id verification to slow down repeated
    /// attempts (docs/SECURITY.md §3). Null or zero applies no delay.
    /// </summary>
    TimeSpan? GetVerificationDelay(LoginAttemptState state, DateTimeOffset now);

    /// <summary>Applies failure bookkeeping and decides whether a lock engages.</summary>
    LoginAttemptOutcome RecordFailure(LoginAttemptState state, DateTimeOffset now);

    /// <summary>Applies success bookkeeping (clears consecutive failures).</summary>
    LoginAttemptState RecordSuccess(LoginAttemptState state, DateTimeOffset now);
}

/// <summary>
/// Part-1 baseline policy: increments <c>failed_login_count</c>, stamps
/// <c>last_failed_login_at</c>, and clears both after a successful login. It
/// never delays and never engages a lock; an already-stored <c>locked_until</c>
/// is preserved and honored by <see cref="AuthService"/>.
/// </summary>
/// <remarks>
/// M1-07 part 2 owns the real policy: progressive delay after 5 consecutive
/// failures, a 15-minute lock at 10, a 24-hour lock at 5 lockouts in 24 hours,
/// and the accompanying <c>account_locked</c> events.
/// </remarks>
public sealed class DefaultLoginAttemptPolicy : ILoginAttemptPolicy
{
    public TimeSpan? GetVerificationDelay(LoginAttemptState state, DateTimeOffset now) => null;

    public LoginAttemptOutcome RecordFailure(LoginAttemptState state, DateTimeOffset now) =>
        new(
            state with
            {
                FailedLoginCount = state.FailedLoginCount + 1,
                LastFailedLoginAt = now,
            },
            LockEngaged: false,
            DelayAfterFailure: null);

    public LoginAttemptState RecordSuccess(LoginAttemptState state, DateTimeOffset now) =>
        state with
        {
            FailedLoginCount = 0,
            LastFailedLoginAt = null,
        };
}
