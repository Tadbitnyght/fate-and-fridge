namespace FateAndFridge.Api.Auth;

/// <summary>
/// M1-07 part 2 lockout policy with the approved defaults from
/// docs/SECURITY.md §3 and docs/MILESTONES.md:
/// <list type="bullet">
/// <item>failures 1–4: no delay;</item>
/// <item>failure 5+: progressive delay <c>min(250 ms × (failures − 4), 3 s)</c>;</item>
/// <item>10 consecutive failures: 15-minute lock;</item>
/// <item>5 lockouts within a rolling 24-hour window: 24-hour lock;</item>
/// <item>a successful login clears consecutive failures and all escalation state.</item>
/// </list>
/// </summary>
/// <remarks>
/// Stateless and thread-safe: the caller passes the current counter state and
/// timestamp, then persists the returned state. An already-stored future
/// <c>locked_until</c> is still short-circuited by <see cref="AuthService"/>
/// before verification; this policy only decides when a new lock engages, and
/// it engages or escalates only while the account is not already locked, so a
/// parallel burst produces exactly one lock engagement and one
/// <c>account_locked</c> event.
/// </remarks>
public sealed class LockoutLoginAttemptPolicy : ILoginAttemptPolicy
{
    /// <summary>First consecutive failure that incurs a progressive delay.</summary>
    public const int ProgressiveDelayStartFailureCount = 5;

    /// <summary>Progressive delay added per failure beyond the start threshold.</summary>
    public const int ProgressiveDelayStepMilliseconds = 250;

    /// <summary>Upper bound for the progressive delay.</summary>
    public const int ProgressiveDelayCapMilliseconds = 3000;

    /// <summary>Consecutive failures that engage an account lock.</summary>
    public const int LockoutFailureThreshold = 10;

    /// <summary>Duration of a standard lock.</summary>
    public const int StandardLockMinutes = 15;

    /// <summary>Lockouts within one window that engage the severe lock.</summary>
    public const int SevereLockoutThreshold = 5;

    /// <summary>Rolling window over which lockouts are counted.</summary>
    public const int LockoutWindowHours = 24;

    /// <summary>
    /// Duration of the severe lock after repeated lockouts. The 24-hour window
    /// is fixed in M1: M1-11 security settings will make the maximum lock
    /// window configurable (24 or 48 hours) per docs/SECURITY.md §3.
    /// </summary>
    public const int SevereLockMinutes = 24 * 60;

    /// <inheritdoc />
    public LoginAttemptOutcome RecordFailure(LoginAttemptState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        var failedLoginCount = state.FailedLoginCount + 1;
        var lockoutCount = state.LockoutCount;
        var lockoutWindowStartedAt = state.LockoutWindowStartedAt;
        var lockedUntil = state.LockedUntil;
        var lockEngaged = false;

        // Engage or escalate only when the account is not currently locked.
        // Parallel failures that raced past the pre-verification lock check all
        // reach this method, but the caller serializes them with a row lock:
        // the first one crossing the threshold owns the engagement, the rest
        // only increment the counter (no extra lock, no extra event).
        var isCurrentlyLocked = state.LockedUntil is { } existingLock && existingLock > now;
        if (!isCurrentlyLocked && failedLoginCount >= LockoutFailureThreshold)
        {
            if (lockoutWindowStartedAt is null
                || now - lockoutWindowStartedAt.Value >= TimeSpan.FromHours(LockoutWindowHours))
            {
                lockoutCount = 1;
                lockoutWindowStartedAt = now;
            }
            else
            {
                lockoutCount += 1;
            }

            lockedUntil = now + LockDuration(lockoutCount);
            lockEngaged = true;
        }

        return new LoginAttemptOutcome(
            state with
            {
                FailedLoginCount = failedLoginCount,
                LastFailedLoginAt = now,
                LockoutCount = lockoutCount,
                LockoutWindowStartedAt = lockoutWindowStartedAt,
                LockedUntil = lockedUntil,
            },
            lockEngaged,
            ProgressiveDelay(failedLoginCount));
    }

    /// <inheritdoc />
    public LoginAttemptState RecordSuccess(LoginAttemptState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state with
        {
            FailedLoginCount = 0,
            LastFailedLoginAt = null,
            LockoutCount = 0,
            LockoutWindowStartedAt = null,
            LockedUntil = null,
        };
    }

    /// <summary>
    /// Draft-style progressive delay: <c>min(250 ms × (count − 4), 3000 ms)</c>.
    /// </summary>
    private static TimeSpan? ProgressiveDelay(int failedLoginCount)
    {
        if (failedLoginCount < ProgressiveDelayStartFailureCount)
        {
            return null;
        }

        var delay = TimeSpan.FromMilliseconds(
            ProgressiveDelayStepMilliseconds
            * (failedLoginCount - ProgressiveDelayStartFailureCount + 1));

        return delay < TimeSpan.FromMilliseconds(ProgressiveDelayCapMilliseconds)
            ? delay
            : TimeSpan.FromMilliseconds(ProgressiveDelayCapMilliseconds);
    }

    private static TimeSpan LockDuration(int lockoutCount) =>
        lockoutCount >= SevereLockoutThreshold
            ? TimeSpan.FromMinutes(SevereLockMinutes)
            : TimeSpan.FromMinutes(StandardLockMinutes);
}
