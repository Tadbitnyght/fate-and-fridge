using System.Collections.Concurrent;
using FateAndFridge.Api.LiveSync;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Recording <see cref="ILiveSyncPublisher"/> test double (M9-01). It can be
/// registered through the test host (see <c>ApiFactory</c>) so later M9 issues
/// assert the exact events a write path emitted without opening a socket.
/// </summary>
public sealed class RecordingLiveSyncPublisher : ILiveSyncPublisher
{
    private readonly ConcurrentQueue<LiveSyncEvent> _events = new();
    private readonly ConcurrentQueue<long> _revokedSessions = new();

    /// <summary>Events passed to <see cref="PublishAsync"/>, in order.</summary>
    public IReadOnlyCollection<LiveSyncEvent> Events => _events;

    /// <summary>Session ids passed to <see cref="PublishSessionRevokedAsync"/>, in order.</summary>
    public IReadOnlyCollection<long> RevokedSessionIds => _revokedSessions;

    /// <inheritdoc />
    public Task PublishAsync(LiveSyncEvent evt, CancellationToken cancellationToken = default)
    {
        _events.Enqueue(evt);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PublishSessionRevokedAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        _revokedSessions.Enqueue(sessionId);
        return Task.CompletedTask;
    }
}
