using FateAndFridge.Api.Auth;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Captures requested progressive login delays without waiting, so unit and
/// integration tests can run full lockout sequences without real-time sleeps
/// (M1-07 part 2).
/// </summary>
public sealed class RecordingLoginAttemptDelayer : ILoginAttemptDelayer
{
    private readonly List<TimeSpan> _waits = [];

    public IReadOnlyList<TimeSpan> Waits => _waits;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        _waits.Add(delay);
        return Task.CompletedTask;
    }
}
