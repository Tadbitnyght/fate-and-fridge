using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// A single registered live-sync connection and the metadata needed to revoke
/// it. <see cref="Context"/> is retained solely so a revoked session's socket can
/// be aborted from outside a hub method.
/// </summary>
public sealed record LiveSyncConnection(
    string ConnectionId,
    long SessionId,
    long? ActorUserId,
    HubCallerContext Context);

/// <summary>
/// Process-local, thread-safe registry of live-sync connections
/// (docs/M9_PLAN.md §2.1). Maps a session id to its connection ids plus actor
/// metadata so a revoked session's sockets can be found and aborted, and enforces
/// the bounded per-session and household-wide connection caps. The single-user
/// system actor has no database session and uses <see cref="SystemSessionId"/>.
/// </summary>
public sealed class LiveSyncConnectionRegistry
{
    /// <summary>Sentinel session id for the single-user system actor.</summary>
    public const long SystemSessionId = 0;

    /// <summary>Bounded number of concurrent connections allowed for one session.</summary>
    public const int MaxConnectionsPerSession = 8;

    /// <summary>
    /// Bounded number of concurrent connections allowed for the whole household
    /// (docs/M9_PLAN.md §1.11 caps connections "per session/household"). Four
    /// sessions at the per-session cap: generous for a household with several
    /// tabs and devices on the single instance, and still bounded.
    /// </summary>
    public const int MaxConnectionsPerHousehold = 32;

    private readonly ConcurrentDictionary<string, LiveSyncConnection> _connections =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, LiveSyncConnection>> _bySession = new();

    private readonly object _registerGate = new();

    /// <summary>Total number of registered connections.</summary>
    public int Count => _connections.Count;

    /// <summary>
    /// Registers a connection unless its session is already at
    /// <see cref="MaxConnectionsPerSession"/> or the household is at
    /// <see cref="MaxConnectionsPerHousehold"/>. Returns <c>false</c> when a cap
    /// is hit; the caller must refuse the connection.
    /// </summary>
    public bool TryRegister(
        string connectionId,
        long sessionId,
        long? actorUserId,
        HubCallerContext context)
    {
        lock (_registerGate)
        {
            if (_connections.Count >= MaxConnectionsPerHousehold)
            {
                return false;
            }

            var sessionConnections = _bySession.GetOrAdd(
                sessionId,
                _ => new ConcurrentDictionary<string, LiveSyncConnection>(StringComparer.Ordinal));
            if (sessionConnections.Count >= MaxConnectionsPerSession)
            {
                return false;
            }

            var connection = new LiveSyncConnection(connectionId, sessionId, actorUserId, context);
            _connections[connectionId] = connection;
            sessionConnections[connectionId] = connection;
            return true;
        }
    }

    /// <summary>Removes a connection; safe to call for an unknown id.</summary>
    public void Unregister(string connectionId)
    {
        // Serialize with TryRegister: otherwise this could see a session's map
        // as empty and drop it from _bySession after a concurrent TryRegister
        // had already re-added a connection to that same map, orphaning a live
        // connection (untracked, so it could not be revoked and the caps would
        // under-count). No reentrancy: neither method calls the other.
        lock (_registerGate)
        {
            if (!_connections.TryRemove(connectionId, out var connection))
            {
                return;
            }

            if (_bySession.TryGetValue(connection.SessionId, out var sessionConnections))
            {
                sessionConnections.TryRemove(connectionId, out _);
                if (sessionConnections.IsEmpty)
                {
                    _bySession.TryRemove(connection.SessionId, out _);
                }
            }
        }
    }

    /// <summary>Number of connections currently registered for a session.</summary>
    public int CountForSession(long sessionId) =>
        _bySession.TryGetValue(sessionId, out var sessionConnections) ? sessionConnections.Count : 0;

    /// <summary>Snapshot of a session's connections (empty when none are registered).</summary>
    public IReadOnlyList<LiveSyncConnection> GetConnections(long sessionId) =>
        _bySession.TryGetValue(sessionId, out var sessionConnections)
            ? sessionConnections.Values.ToList()
            : [];
}
