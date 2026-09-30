using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Part-1 baseline <see cref="ILoginAttemptPolicy"/>: consecutive-failure
/// counters only, no thresholds, progressive delay, or lock engagement
/// (those arrive in M1-07 part 2; docs/SECURITY.md §3).
/// </summary>
public sealed class DefaultLoginAttemptPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly DefaultLoginAttemptPolicy _policy = new();

    [Fact]
    public void RecordFailure_increments_the_counter_and_stamps_last_failed_at()
    {
        var state = new LoginAttemptState(
            FailedLoginCount: 2,
            LastFailedLoginAt: Now.AddMinutes(-5),
            LockoutCount: 0,
            LockoutWindowStartedAt: null,
            LockedUntil: null);

        var outcome = _policy.RecordFailure(state, Now);

        outcome.State.FailedLoginCount.Should().Be(3);
        outcome.State.LastFailedLoginAt.Should().Be(Now);
        outcome.LockEngaged.Should().BeFalse();
        outcome.DelayAfterFailure.Should().BeNull();
        outcome.State.LockedUntil.Should().BeNull();
    }

    [Fact]
    public void RecordFailure_preserves_the_lockout_window_and_an_existing_lock()
    {
        var lockedUntil = Now.AddMinutes(15);
        var state = new LoginAttemptState(9, Now.AddMinutes(-1), 1, Now.AddDays(-2), lockedUntil);

        var outcome = _policy.RecordFailure(state, Now);

        outcome.State.LockoutCount.Should().Be(1);
        outcome.State.LockoutWindowStartedAt.Should().Be(Now.AddDays(-2));
        outcome.State.LockedUntil.Should().Be(lockedUntil);
        outcome.LockEngaged.Should().BeFalse();
    }

    [Fact]
    public void RecordSuccess_clears_consecutive_failures_but_keeps_the_lockout_window()
    {
        var state = new LoginAttemptState(7, Now.AddMinutes(-1), 2, Now.AddDays(-1), null);

        var next = _policy.RecordSuccess(state, Now);

        next.FailedLoginCount.Should().Be(0);
        next.LastFailedLoginAt.Should().BeNull();
        next.LockoutCount.Should().Be(2);
        next.LockoutWindowStartedAt.Should().Be(Now.AddDays(-1));
    }

    [Fact]
    public void GetVerificationDelay_stays_null_until_part_two() =>
        _policy.GetVerificationDelay(new LoginAttemptState(99, Now, 0, null, null), Now)
            .Should().BeNull();

    [Fact]
    public void LoginAttemptState_round_trips_through_a_user()
    {
        var user = new User
        {
            Username = "smith",
            NormalizedUsername = "smith",
            PasswordHash = "phc-placeholder",
            FailedLoginCount = 3,
            LastFailedLoginAt = Now,
            LockoutCount = 1,
            LockoutWindowStartedAt = Now.AddDays(-1),
            LockedUntil = Now.AddMinutes(15),
        };

        var state = LoginAttemptState.From(user);

        state.FailedLoginCount.Should().Be(3);
        state.LastFailedLoginAt.Should().Be(Now);
        state.LockoutCount.Should().Be(1);
        state.LockoutWindowStartedAt.Should().Be(Now.AddDays(-1));
        state.LockedUntil.Should().Be(Now.AddMinutes(15));

        (state with { FailedLoginCount = 4, LastFailedLoginAt = Now.AddMinutes(1) }).ApplyTo(user);

        user.FailedLoginCount.Should().Be(4);
        user.LastFailedLoginAt.Should().Be(Now.AddMinutes(1));
        user.LockoutCount.Should().Be(1);
        user.LockedUntil.Should().Be(Now.AddMinutes(15));
    }
}
