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
/// Result of recording a credential failure: the updated counter state plus the
/// lockout decisions from <see cref="LockoutLoginAttemptPolicy"/>.
/// </summary>
/// <param name="State">Counters to apply to the user row.</param>
/// <param name="LockEngaged">True when this failure triggered a lock; the login path writes an <c>account_locked</c> event.</param>
/// <param name="DelayAfterFailure">Progressive delay applied before the response; null below the delay threshold.</param>
public sealed record LoginAttemptOutcome(
    LoginAttemptState State,
    bool LockEngaged,
    TimeSpan? DelayAfterFailure);

/// <summary>
/// Login-attempt policy seam. Production injects
/// <see cref="LockoutLoginAttemptPolicy"/>, which computes failure bookkeeping
/// progressive delay, and lockout windows from docs/SECURITY.md §3 while
/// <see cref="AuthService"/> owns persistence and auth events.
/// </summary>
public interface ILoginAttemptPolicy
{
    /// <summary>Applies failure bookkeeping and decides whether a lock engages.</summary>
    LoginAttemptOutcome RecordFailure(LoginAttemptState state, DateTimeOffset now);

    /// <summary>Applies success bookkeeping (clears consecutive failures and escalation state).</summary>
    LoginAttemptState RecordSuccess(LoginAttemptState state, DateTimeOffset now);
}
