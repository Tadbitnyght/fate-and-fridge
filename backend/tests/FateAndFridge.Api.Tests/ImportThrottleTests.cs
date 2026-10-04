using FateAndFridge.Api.Imports;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Unit coverage of the per-actor import throttle (docs/M4_PLAN.md §1.15,
/// docs/API.md §15): fixed-window boundaries, expiry, page/image isolation,
/// bounded eviction, and the single-user/system actor.
/// </summary>
public sealed class ImportThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ImportThrottle Create(TestTimeProvider clock, Action<ImportThrottleOptions>? configure = null)
    {
        var options = new ImportThrottleOptions
        {
            PageFetchesPerHour = 2,
            ImageFetchesPerHour = 3,
            WindowSeconds = 3600,
            MaxTrackedActors = 1000,
        };
        configure?.Invoke(options);
        return new ImportThrottle(clock, Options.Create(options));
    }

    [Fact]
    public void Fixed_window_allows_exactly_the_limit_then_blocks()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = Create(clock);

        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeTrue();
        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeTrue();

        var blocked = throttle.Reserve("actor", ImportFetchBudget.Page);
        blocked.Allowed.Should().BeFalse();
        blocked.RetryAfter.Should().BeGreaterThan(TimeSpan.Zero);
        blocked.RetryAfter.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Window_resets_only_after_it_expires()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = Create(clock, options => options.PageFetchesPerHour = 1);

        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeTrue();
        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(3599));
        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeFalse();

        clock.Advance(TimeSpan.FromSeconds(2));
        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Page_and_image_budgets_are_isolated()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = Create(clock, options =>
        {
            options.PageFetchesPerHour = 1;
            options.ImageFetchesPerHour = 2;
        });

        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeTrue();
        throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeFalse();

        throttle.Reserve("actor", ImportFetchBudget.Image).Allowed.Should().BeTrue();
        throttle.Reserve("actor", ImportFetchBudget.Image).Allowed.Should().BeTrue();
        throttle.Reserve("actor", ImportFetchBudget.Image).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Actors_have_independent_windows()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = Create(clock, options => options.PageFetchesPerHour = 1);

        throttle.Reserve("actor-a", ImportFetchBudget.Page).Allowed.Should().BeTrue();
        throttle.Reserve("actor-a", ImportFetchBudget.Page).Allowed.Should().BeFalse();
        throttle.Reserve("actor-b", ImportFetchBudget.Page).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Tracked_windows_are_evicted_at_the_cap()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = Create(clock, options =>
        {
            options.PageFetchesPerHour = 1;
            options.MaxTrackedActors = 2;
        });

        throttle.Reserve("a", ImportFetchBudget.Page).Allowed.Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(10));
        throttle.Reserve("b", ImportFetchBudget.Page).Allowed.Should().BeTrue();

        // Adding "b" puts the dictionary at the cap and evicts "a" (the
        // oldest window), so a fresh reservation for "a" is allowed again.
        clock.Advance(TimeSpan.FromSeconds(10));
        throttle.Reserve("a", ImportFetchBudget.Page).Allowed.Should().BeTrue();
        throttle.Reserve("a", ImportFetchBudget.Page).Allowed.Should().BeFalse();
    }

    [Fact]
    public void The_system_actor_is_throttled_like_any_other()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = Create(clock, options => options.PageFetchesPerHour = 1);

        throttle.Reserve("system", ImportFetchBudget.Page).Allowed.Should().BeTrue();
        throttle.Reserve("system", ImportFetchBudget.Page).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Disabled_throttle_allows_unlimited_reservations()
    {
        var clock = new TestTimeProvider(Now);
        var throttle = Create(clock, options =>
        {
            options.Enabled = false;
            options.PageFetchesPerHour = 1;
        });

        for (var i = 0; i < 10; i++)
        {
            throttle.Reserve("actor", ImportFetchBudget.Page).Allowed.Should().BeTrue();
        }
    }
}
