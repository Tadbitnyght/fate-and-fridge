using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FluentAssertions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M1-07 part 2 lockout policy with the approved defaults from
/// docs/SECURITY.md §3: progressive delay from the 5th failure, a 15-minute
/// lock at 10 consecutive failures, and a 24-hour lock at 5 lockouts inside a
/// 24-hour window.
/// </summary>
public sealed class LockoutLoginAttemptPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly LockoutLoginAttemptPolicy _policy = new();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RecordFailure_below_the_start_threshold_adds_no_delay(int failuresBefore)
    {
        var outcome = _policy.RecordFailure(State(failuresBefore), Now);

        outcome.DelayAfterFailure.Should().BeNull();
    }

    [Theory]
    [InlineData(4, 250)]
    [InlineData(5, 500)]
    [InlineData(9, 1500)]
    [InlineData(15, 3000)]
    [InlineData(30, 3000)]
    public void RecordFailure_applies_the_progressive_delay_and_caps_at_three_seconds(
        int failuresBefore,
        int expectedDelayMilliseconds)
    {
        var outcome = _policy.RecordFailure(State(failuresBefore), Now);

        outcome.DelayAfterFailure.Should().Be(TimeSpan.FromMilliseconds(expectedDelayMilliseconds));
    }

    [Fact]
    public void Ninth_consecutive_failure_does_not_lock()
    {
        var outcome = _policy.RecordFailure(State(8), Now);

        outcome.State.FailedLoginCount.Should().Be(9);
        outcome.State.LastFailedLoginAt.Should().Be(Now);
        outcome.LockEngaged.Should().BeFalse();
        outcome.State.LockedUntil.Should().BeNull();
        outcome.State.LockoutCount.Should().Be(0);
        outcome.State.LockoutWindowStartedAt.Should().BeNull();
    }

    [Fact]
    public void Tenth_consecutive_failure_locks_for_fifteen_minutes()
    {
        var outcome = _policy.RecordFailure(State(9), Now);

        outcome.State.FailedLoginCount.Should().Be(10);
        outcome.LockEngaged.Should().BeTrue();
        outcome.State.LockedUntil.Should().Be(Now.AddMinutes(15));
        outcome.State.LockoutCount.Should().Be(1);
        outcome.State.LockoutWindowStartedAt.Should().Be(Now);
        outcome.DelayAfterFailure.Should().Be(TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public void Failure_while_still_locked_only_counts_and_does_not_re_engage()
    {
        var lockedUntil = Now.AddMinutes(10);
        var windowStartedAt = Now.AddHours(-1);
        var state = new LoginAttemptState(
            FailedLoginCount: 10,
            LastFailedLoginAt: Now.AddMinutes(-1),
            LockoutCount: 1,
            LockoutWindowStartedAt: windowStartedAt,
            LockedUntil: lockedUntil);

        var outcome = _policy.RecordFailure(state, Now);

        outcome.State.FailedLoginCount.Should().Be(11);
        outcome.State.LastFailedLoginAt.Should().Be(Now);
        outcome.LockEngaged.Should().BeFalse();
        outcome.State.LockedUntil.Should().Be(lockedUntil);
        outcome.State.LockoutCount.Should().Be(1);
        outcome.State.LockoutWindowStartedAt.Should().Be(windowStartedAt);
    }

    [Fact]
    public void A_burst_after_the_crossing_failure_only_extends_the_counter()
    {
        var first = _policy.RecordFailure(State(9), Now);
        first.LockEngaged.Should().BeTrue();

        var second = _policy.RecordFailure(first.State, Now.AddMilliseconds(1));

        second.State.FailedLoginCount.Should().Be(11);
        second.LockEngaged.Should().BeFalse();
        second.State.LockedUntil.Should().Be(first.State.LockedUntil);
        second.State.LockoutCount.Should().Be(1);
        second.State.LockoutWindowStartedAt.Should().Be(first.State.LockoutWindowStartedAt);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void Failure_after_the_lock_has_expired_engages_again_and_escalates(int lockOffsetMinutes)
    {
        var windowStartedAt = Now.AddHours(-1);
        var state = new LoginAttemptState(
            FailedLoginCount: 10,
            LastFailedLoginAt: Now.AddMinutes(-1),
            LockoutCount: 1,
            LockoutWindowStartedAt: windowStartedAt,
            LockedUntil: Now.AddMinutes(lockOffsetMinutes));

        var outcome = _policy.RecordFailure(state, Now);

        outcome.State.FailedLoginCount.Should().Be(11);
        outcome.LockEngaged.Should().BeTrue();
        outcome.State.LockoutCount.Should().Be(2);
        outcome.State.LockoutWindowStartedAt.Should().Be(windowStartedAt);
        outcome.State.LockedUntil.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public void Locks_within_the_window_increment_the_lockout_count()
    {
        var state = new LoginAttemptState(
            FailedLoginCount: 9,
            LastFailedLoginAt: Now.AddMinutes(-1),
            LockoutCount: 2,
            LockoutWindowStartedAt: Now.AddHours(-1),
            LockedUntil: Now.AddMinutes(-5));

        var outcome = _policy.RecordFailure(state, Now);

        outcome.LockEngaged.Should().BeTrue();
        outcome.State.LockoutCount.Should().Be(3);
        outcome.State.LockoutWindowStartedAt.Should().Be(Now.AddHours(-1));
        outcome.State.LockedUntil.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public void Fifth_lock_in_the_window_locks_for_twenty_four_hours()
    {
        var state = new LoginAttemptState(
            FailedLoginCount: 9,
            LastFailedLoginAt: Now.AddMinutes(-1),
            LockoutCount: 4,
            LockoutWindowStartedAt: Now.AddHours(-2),
            LockedUntil: null);

        var outcome = _policy.RecordFailure(state, Now);

        outcome.LockEngaged.Should().BeTrue();
        outcome.State.LockoutCount.Should().Be(5);
        outcome.State.LockoutWindowStartedAt.Should().Be(Now.AddHours(-2));
        outcome.State.LockedUntil.Should().Be(Now.AddHours(24));
    }

    [Fact]
    public void Expired_lockout_window_restarts_the_lockout_count()
    {
        var state = new LoginAttemptState(
            FailedLoginCount: 9,
            LastFailedLoginAt: Now.AddMinutes(-1),
            LockoutCount: 4,
            LockoutWindowStartedAt: Now.AddHours(-24).AddSeconds(-1),
            LockedUntil: null);

        var outcome = _policy.RecordFailure(state, Now);

        outcome.State.LockoutCount.Should().Be(1);
        outcome.State.LockoutWindowStartedAt.Should().Be(Now);
        outcome.State.LockedUntil.Should().Be(Now.AddMinutes(15));
    }

    [Fact]
    public void Missing_lockout_window_starts_a_new_window()
    {
        var outcome = _policy.RecordFailure(State(9), Now);

        outcome.State.LockoutCount.Should().Be(1);
        outcome.State.LockoutWindowStartedAt.Should().Be(Now);
    }

    [Fact]
    public void Success_clears_all_lockout_and_escalation_state()
    {
        var state = new LoginAttemptState(
            FailedLoginCount: 7,
            LastFailedLoginAt: Now.AddMinutes(-1),
            LockoutCount: 3,
            LockoutWindowStartedAt: Now.AddHours(-2),
            LockedUntil: Now.AddMinutes(15));

        var next = _policy.RecordSuccess(state, Now);

        next.FailedLoginCount.Should().Be(0);
        next.LastFailedLoginAt.Should().BeNull();
        next.LockoutCount.Should().Be(0);
        next.LockoutWindowStartedAt.Should().BeNull();
        next.LockedUntil.Should().BeNull();
    }

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

    private static LoginAttemptState State(int failedLoginCount) =>
        new(
            FailedLoginCount: failedLoginCount,
            LastFailedLoginAt: null,
            LockoutCount: 0,
            LockoutWindowStartedAt: null,
            LockedUntil: null);
}
