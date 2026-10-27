namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Deterministic clock for session expiry and touch-throttle tests. The value
/// is always stored as a zero-offset UTC instant because
/// <see cref="TimeProvider.GetUtcNow"/> must return UTC: Npgsql rejects a
/// non-zero offset when the value is bound to <c>timestamp with time zone</c>.
/// Normalising here clips only the offset; it never shifts the instant.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow.ToUniversalTime();

    public DateTimeOffset UtcNow
    {
        get => _utcNow;
        set => _utcNow = value.ToUniversalTime();
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
