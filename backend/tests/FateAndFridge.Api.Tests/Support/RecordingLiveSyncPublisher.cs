using System.Collections.Concurrent;
using FateAndFridge.Api.LiveSync;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>One user-scoped revocation recorded by <see cref="RecordingLiveSyncPublisher"/>.</summary>
public sealed record RecordedUserRevocation(long UserId, long? ExceptSessionId);

/// <summary>
/// Recording <see cref="ILiveSyncPublisher"/> test double (M9-01). It can be
/// registered through the test host (see <c>ApiFactory</c>) so later M9 issues
/// assert the exact events a write path emitted without opening a socket.
/// </summary>
public sealed class RecordingLiveSyncPublisher : ILiveSyncPublisher
{
    private readonly ConcurrentQueue<LiveSyncEvent> _events = new();
    private readonly ConcurrentQueue<long> _revokedSessions = new();
    private readonly ConcurrentQueue<RecordedUserRevocation> _revokedUserSessions = new();

    /// <summary>Events passed to <see cref="PublishAsync"/>, in order.</summary>
    public IReadOnlyCollection<LiveSyncEvent> Events => _events;

    /// <summary>Session ids passed to <see cref="PublishSessionRevokedAsync"/>, in order.</summary>
    public IReadOnlyCollection<long> RevokedSessionIds => _revokedSessions;

    /// <summary>User-scoped revocations passed to <see cref="PublishSessionRevokedForUserAsync"/>.</summary>
    public IReadOnlyCollection<RecordedUserRevocation> RevokedUserSessions => _revokedUserSessions;

    /// <summary>
    /// Clears every recorded event and revocation so one test can assert each
    /// write independently without rebuilding the host.
    /// </summary>
    public void Clear()
    {
        while (_events.TryDequeue(out _))
        {
        }

        while (_revokedSessions.TryDequeue(out _))
        {
        }

        while (_revokedUserSessions.TryDequeue(out _))
        {
        }
    }

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

    /// <inheritdoc />
    public Task PublishSessionRevokedForUserAsync(
        long userId,
        long? exceptSessionId = null,
        CancellationToken cancellationToken = default)
    {
        _revokedUserSessions.Enqueue(new RecordedUserRevocation(userId, exceptSessionId));
        return Task.CompletedTask;
    }
}
