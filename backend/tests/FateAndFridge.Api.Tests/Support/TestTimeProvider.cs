namespace FateAndFridge.Api.Tests.Support;

/// <summary>Deterministic clock for session expiry and touch-throttle tests.</summary>
public sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void Advance(TimeSpan delta) => UtcNow += delta;
}
