using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Configuration for the per-IP login throttle (docs/SECURITY.md §3): 20
/// attempts per source address per 10 minutes by default, after which the
/// address receives <c>429 RATE_LIMITED</c> with a <c>Retry-After</c> header
/// until the window expires.
/// </summary>
public sealed class LoginThrottleOptions
{
    public const string SectionName = "Auth:LoginThrottle";

    /// <summary>Disables tracking entirely when false (test hosts).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Login attempts from one address that may reserve a slot within the fixed
    /// window; further attempts are blocked until it resets. A successful login
    /// clears the address's reserved slots, so effectively only failures
    /// consume the budget.
    /// </summary>
    public int MaxFailuresPerWindow { get; set; } = 20;

    /// <summary>Fixed window length in seconds.</summary>
    public int WindowSeconds { get; set; } = 600;

    /// <summary>
    /// Hard bound on tracked addresses, so spoofed source addresses cannot grow
    /// memory without limit; the entry closest to expiry is evicted first.
    /// </summary>
    public int MaxTrackedAddresses { get; set; } = 10_000;
}

/// <summary>Verdict for one login attempt from a source address.</summary>
/// <param name="Allowed">True when the attempt may proceed to verification.</param>
/// <param name="RetryAfter">Remaining window when <paramref name="Allowed"/> is false.</param>
public sealed record LoginIpThrottleDecision(bool Allowed, TimeSpan RetryAfter)
{
    /// <summary>Attempt may proceed; no retry delay.</summary>
    public static readonly LoginIpThrottleDecision Allow = new(Allowed: true, RetryAfter: TimeSpan.Zero);
}

/// <summary>
/// Per-IP login limiter (M1-07 part 2). Each attempt atomically reserves a slot
/// before verification; over-threshold attempts are rejected immediately.
/// A successful login resets the address, so slots effectively count failures.
/// </summary>
public interface ILoginIpThrottle
{
    /// <summary>
    /// Atomically reserves one slot for an attempt from <paramref name="ipAddress"/>.
    /// Called before password verification; a blocked decision means the caller
    /// must answer <c>429</c> without doing any further work. Unknown and blank
    /// addresses are always allowed and never tracked.
    /// </summary>
    LoginIpThrottleDecision Reserve(string? ipAddress);

    /// <summary>
    /// Clears the window for <paramref name="ipAddress"/> after a successful
    /// login, releasing its reservations.
    /// </summary>
    void RecordSuccess(string? ipAddress);
}

/// <summary>
/// In-memory fixed-window reservation counter keyed by source address, stamped
/// by the injected <see cref="TimeProvider"/>. Each entry expires with its
/// window and the dictionary is bounded by
/// <see cref="LoginThrottleOptions.MaxTrackedAddresses"/>, so neither time nor
/// address rotation grows memory without limit.
/// </summary>
/// <remarks>
/// <para>
/// State is intentionally process-local: V1 runs one app container, so
/// per-IP throttling is not shared across replicas. Horizontal scaling would
/// need shared state (for example a database or cache-backed counter).
/// </para>
/// <para>
/// Reservation is serialized per address with a window-local lock, so exactly
/// <see cref="LoginThrottleOptions.MaxFailuresPerWindow"/> parallel attempts
/// pass and the rest are blocked. Window replacement at expiry and eviction at
/// the memory cap remain best-effort (a window may be dropped at the boundary).
/// </para>
/// </remarks>
public sealed class LoginIpThrottle : ILoginIpThrottle
{
    private readonly ConcurrentDictionary<string, FailureWindow> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly bool _enabled;
    private readonly int _maxFailuresPerWindow;
    private readonly TimeSpan _window;
    private readonly int _maxTrackedAddresses;

    public LoginIpThrottle(TimeProvider timeProvider, IOptions<LoginThrottleOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var value = options.Value;
        _timeProvider = timeProvider;
        _enabled = value.Enabled;
        _maxFailuresPerWindow = value.MaxFailuresPerWindow;
        _window = TimeSpan.FromSeconds(value.WindowSeconds);
        _maxTrackedAddresses = value.MaxTrackedAddresses;
    }

    /// <inheritdoc />
    public LoginIpThrottleDecision Reserve(string? ipAddress)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(ipAddress))
        {
            return LoginIpThrottleDecision.Allow;
        }

        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            var window = _windows.GetOrAdd(ipAddress, _ => new FailureWindow(now + _window));
            lock (window.SyncRoot)
            {
                // Sample the clock while holding the lock: another request may
                // have created the window after our pre-lock sample, which
                // would otherwise report a RetryAfter longer than the window.
                var lockedNow = _timeProvider.GetUtcNow();

                // Another thread may have replaced an expired window; retry
                // against whichever window is current.
                if (!_windows.TryGetValue(ipAddress, out var current) || !ReferenceEquals(current, window))
                {
                    continue;
                }

                if (lockedNow >= window.ExpiresAt)
                {
                    _windows.TryRemove(new KeyValuePair<string, FailureWindow>(ipAddress, window));
                    continue;
                }

                if (window.Count >= _maxFailuresPerWindow)
                {
                    var remaining = window.ExpiresAt - lockedNow;
                    if (remaining > _window)
                    {
                        remaining = _window;
                    }

                    return new LoginIpThrottleDecision(Allowed: false, RetryAfter: remaining);
                }

                window.Increment();
                Prune(lockedNow);
                return LoginIpThrottleDecision.Allow;
            }
        }
    }

    /// <inheritdoc />
    public void RecordSuccess(string? ipAddress)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(ipAddress))
        {
            return;
        }

        _windows.TryRemove(ipAddress, out _);
    }

    /// <summary>
    /// Keeps the dictionary bounded once the cap is reached: expired entries
    /// first, then the entry closest to expiry. Best-effort: eviction may drop
    /// an active window under sustained address rotation.
    /// </summary>
    private void Prune(DateTimeOffset now)
    {
        if (_windows.Count < _maxTrackedAddresses)
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

        while (_windows.Count >= _maxTrackedAddresses)
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

    /// <summary>One fixed window with a per-address serialization lock.</summary>
    private sealed class FailureWindow(DateTimeOffset expiresAt)
    {
        private int _count;

        public object SyncRoot { get; } = new();

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public int Count => _count;

        public void Increment() => _count++;
    }
}
