using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Fake-clock verification of the part 2 per-IP throttle: attempts
/// atomically reserve a slot before verification (fixed 10-minute window by
/// default), over-threshold attempts are blocked, success resets the address
/// and the address table stays bounded.
/// </summary>
public sealed class LoginIpThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reservations_below_the_threshold_are_allowed()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 3);

        for (var i = 0; i < 3; i++)
        {
            throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue();
        }
    }

    [Fact]
    public void Reaching_the_threshold_blocks_with_the_exact_remaining_window()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 3, windowSeconds: 60);

        for (var i = 0; i < 3; i++)
        {
            throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue();
        }

        var blocked = throttle.Reserve("1.2.3.4");
        blocked.Allowed.Should().BeFalse();
        blocked.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));

        clock.Advance(TimeSpan.FromSeconds(10));
        var later = throttle.Reserve("1.2.3.4");
        later.Allowed.Should().BeFalse();
        later.RetryAfter.Should().Be(TimeSpan.FromSeconds(50));
    }

    [Fact]
    public void Window_expiry_clears_the_block_and_starts_a_new_window()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 3, windowSeconds: 60);
        for (var i = 0; i < 3; i++)
        {
            throttle.Reserve("1.2.3.4");
        }

        clock.Advance(TimeSpan.FromSeconds(60));

        throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue();
        throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Success_clears_the_address_reservations()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 3, windowSeconds: 60);
        throttle.Reserve("1.2.3.4");
        throttle.Reserve("1.2.3.4");

        throttle.RecordSuccess("1.2.3.4");

        throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue("the successful login cleared the earlier reservations");
        throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue();
        throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue();
        throttle.Reserve("1.2.3.4").Allowed.Should().BeFalse();
    }

    [Fact]
    public void Parallel_reservations_allow_exactly_the_threshold()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 3, windowSeconds: 60);
        var decisions = new LoginIpThrottleDecision[12];

        Parallel.For(0, decisions.Length, index => decisions[index] = throttle.Reserve("1.2.3.4"));

        decisions.Count(decision => decision.Allowed).Should().Be(3);
        decisions.Count(decision => !decision.Allowed).Should().Be(9);
    }

    [Fact]
    public void Blocked_decisions_never_exceed_the_window()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 3, windowSeconds: 60);
        var decisions = new LoginIpThrottleDecision[12];

        Parallel.For(0, decisions.Length, index => decisions[index] = throttle.Reserve("1.2.3.4"));

        decisions.Where(decision => !decision.Allowed)
            .Should().OnlyContain(decision =>
                decision.RetryAfter > TimeSpan.Zero
                && decision.RetryAfter <= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Other_addresses_are_unaffected_by_a_blocked_one()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 3, windowSeconds: 60);
        for (var i = 0; i < 3; i++)
        {
            throttle.Reserve("1.2.3.4");
        }

        throttle.Reserve("1.2.3.4").Allowed.Should().BeFalse();
        throttle.Reserve("5.6.7.8").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Disabled_throttle_allows_everything()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, enabled: false);

        for (var i = 0; i < 100; i++)
        {
            throttle.Reserve("1.2.3.4").Allowed.Should().BeTrue();
        }
    }

    [Fact]
    public void Null_and_blank_addresses_are_never_tracked()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 1);

        for (var i = 0; i < 100; i++)
        {
            throttle.Reserve(null).Allowed.Should().BeTrue();
            throttle.Reserve("  ").Allowed.Should().BeTrue();
        }
    }

    [Fact]
    public void Tracked_addresses_stay_bounded_by_evicting_the_soonest_to_expire()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = CreateThrottle(clock, maxFailures: 1, maxTrackedAddresses: 2);

        throttle.Reserve("1.1.1.1").Allowed.Should().BeTrue();
        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.Reserve("2.2.2.2").Allowed.Should().BeTrue();
        throttle.Reserve("2.2.2.2").Allowed.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.Reserve("3.3.3.3").Allowed.Should().BeTrue();
        throttle.Reserve("3.3.3.3").Allowed.Should().BeFalse();

        throttle.Reserve("1.1.1.1").Allowed.Should().BeTrue("the oldest tracked address was evicted");
    }

    private static LoginIpThrottle CreateThrottle(
        TestTimeProvider clock,
        int maxFailures = 20,
        int windowSeconds = 600,
        bool enabled = true,
        int maxTrackedAddresses = 1000) =>
        new(
            clock,
            Options.Create(new LoginThrottleOptions
            {
                Enabled = enabled,
                MaxFailuresPerWindow = maxFailures,
                WindowSeconds = windowSeconds,
                MaxTrackedAddresses = maxTrackedAddresses,
            }));
}
