using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// Per-user import fetch limits (docs/M4_PLAN.md §1.15, docs/API.md §15):
/// 30 page fetches and 60 image fetches per rolling one-hour window by default.
/// Excess requests receive <c>429 IMPORT_RATE_LIMITED</c>. Loopback callers and
/// the single-user system actor are throttled too.
/// </summary>
public sealed class ImportThrottleOptions
{
    /// <summary>Configuration section bound to this type.</summary>
    public const string SectionName = "Imports:Throttle";

    /// <summary>Disables tracking entirely when false (test hosts).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Page fetches allowed per actor per window.</summary>
    public int PageFetchesPerHour { get; set; } = 30;

    /// <summary>Image fetches allowed per actor per window (M4-07 consumes this).</summary>
    public int ImageFetchesPerHour { get; set; } = 60;

    /// <summary>Fixed window length in seconds (one hour).</summary>
    public int WindowSeconds { get; set; } = 3600;

    /// <summary>
    /// Hard bound on tracked actor/budget pairs so user churn cannot grow memory
    /// without limit; the entry closest to expiry is evicted first.
    /// </summary>
    public int MaxTrackedActors { get; set; } = 1000;
}

/// <summary>Which outbound budget an import operation consumes.</summary>
public enum ImportFetchBudget
{
    /// <summary>An HTML page fetch.</summary>
    Page = 0,

    /// <summary>An image download.</summary>
    Image = 1,
}

/// <summary>Verdict for one import fetch reservation.</summary>
/// <param name="Allowed">True when the fetch may proceed.</param>
/// <param name="RetryAfter">Remaining window when <paramref name="Allowed"/> is false.</param>
public readonly record struct ImportThrottleDecision(bool Allowed, TimeSpan RetryAfter)
{
    /// <summary>Reservation succeeded; no retry delay.</summary>
    public static readonly ImportThrottleDecision Allow = new(true, TimeSpan.Zero);
}

/// <summary>Process-local fixed-window throttle for import fetches.</summary>
public interface IImportThrottle
{
    /// <summary>
    /// Atomically reserves one slot of <paramref name="budget"/> for
    /// <paramref name="actorKey"/>. Call before performing the outbound fetch;
    /// a blocked decision means the caller answers <c>429</c> without I/O.
    /// </summary>
    ImportThrottleDecision Reserve(string actorKey, ImportFetchBudget budget);
}

/// <summary>
/// In-memory fixed-window counter keyed by actor and budget, stamped by the
/// injected <see cref="TimeProvider"/>. State is intentionally process-local:
/// V1 runs one app container, so limits are not shared across replicas.
/// </summary>
public sealed class ImportThrottle : IImportThrottle
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly bool _enabled;
    private readonly int _pageLimit;
    private readonly int _imageLimit;
    private readonly TimeSpan _window;
    private readonly int _maxTrackedActors;

    public ImportThrottle(TimeProvider timeProvider, IOptions<ImportThrottleOptions> options)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _timeProvider = timeProvider;
        _enabled = value.Enabled;
        _pageLimit = value.PageFetchesPerHour;
        _imageLimit = value.ImageFetchesPerHour;
        _window = TimeSpan.FromSeconds(value.WindowSeconds);
        _maxTrackedActors = value.MaxTrackedActors;
    }

    /// <inheritdoc />
    public ImportThrottleDecision Reserve(string actorKey, ImportFetchBudget budget)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(actorKey))
        {
            return ImportThrottleDecision.Allow;
        }

        var limit = budget == ImportFetchBudget.Page ? _pageLimit : _imageLimit;
        var key = string.Concat(actorKey, "|", ((int)budget).ToString());

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            var window = _windows.GetOrAdd(key, _ => new Window(now + _window));
            lock (window.SyncRoot)
            {
                var lockedNow = _timeProvider.GetUtcNow();

                if (!_windows.TryGetValue(key, out var current) || !ReferenceEquals(current, window))
                {
                    continue;
                }

                if (lockedNow >= window.ExpiresAt)
                {
                    _windows.TryRemove(new KeyValuePair<string, Window>(key, window));
                    continue;
                }

                if (window.Count >= limit)
                {
                    var remaining = window.ExpiresAt - lockedNow;
                    if (remaining > _window)
                    {
                        remaining = _window;
                    }

                    return new ImportThrottleDecision(false, remaining);
                }

                window.Increment();
                Prune(lockedNow);
                return ImportThrottleDecision.Allow;
            }
        }
    }

    /// <summary>
    /// Keeps the dictionary bounded once the cap is reached: expired entries
    /// first, then the entry closest to expiry. Best-effort: eviction may drop
    /// an active window under sustained actor churn.
    /// </summary>
    private void Prune(DateTimeOffset now)
    {
        if (_windows.Count < _maxTrackedActors)
        {
            return;
        }

        foreach (var (key, window) in _windows)
        {
            if (now >= window.ExpiresAt)
            {
                _windows.TryRemove(key, out _);
            }
        }

        while (_windows.Count >= _maxTrackedActors)
        {
            string? oldestKey = null;
            var oldestExpiry = DateTimeOffset.MaxValue;
            foreach (var (key, window) in _windows)
            {
                if (window.ExpiresAt < oldestExpiry)
                {
                    oldestExpiry = window.ExpiresAt;
                    oldestKey = key;
                }
            }

            if (oldestKey is null)
            {
                return;
            }

            _windows.TryRemove(oldestKey, out _);
        }
    }

    private sealed class Window(DateTimeOffset expiresAt)
    {
        private int _count;

        public object SyncRoot { get; } = new();

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public int Count => _count;

        public void Increment() => _count++;
    }
}
