namespace FateAndFridge.Api.Auth;

/// <summary>
/// Executes the progressive login delays returned by
/// <see cref="ILoginAttemptPolicy"/>. Injectable so tests can observe or
/// neutralize delays instead of waiting in real time (part 2).
/// </summary>
public interface ILoginAttemptDelayer
{
    /// <summary>Waits for <paramref name="delay"/> before the login response is written.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>
/// Production delayer: waits through the injected <see cref="TimeProvider"/>
/// so a fake clock in tests can also virtualize the delay.
/// </summary>
public sealed class TimeProviderLoginAttemptDelayer(TimeProvider timeProvider) : ILoginAttemptDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}
