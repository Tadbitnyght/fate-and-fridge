namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// The seam every write path calls after a successful, committed write
/// (docs/M9_PLAN.md §2.1). Publishing is a best-effort notification, never part
/// of the write's transaction, and must never throw into the caller: the SignalR
/// implementation logs and swallows delivery failures and is a no-op when
/// nothing is connected.
/// </summary>
public interface ILiveSyncPublisher
{
    /// <summary>
    /// Broadcasts an event to every connected session of the household,
    /// including the originator (docs/M9_PLAN.md §1.7).
    /// </summary>
    Task PublishAsync(LiveSyncEvent evt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends <see cref="LiveSyncEventType.SessionRevoked"/> to a session's
    /// connections and then aborts them (docs/M9_PLAN.md §1.13). M9-06 wires the
    /// call sites; M9-01 only provides the method.
    /// </summary>
    Task PublishSessionRevokedAsync(long sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends <see cref="LiveSyncEventType.SessionRevoked"/> to every connection
    /// belonging to <paramref name="userId"/> and then aborts them, excluding the
    /// session <paramref name="exceptSessionId"/> when supplied (docs/M9_PLAN.md
    /// §1.13). Used by password reset, user disable, privilege change, and the
    /// password-change "revoke others" path; the per-connection user id comes
    /// from the registry metadata.
    /// </summary>
    Task PublishSessionRevokedForUserAsync(
        long userId,
        long? exceptSessionId = null,
        CancellationToken cancellationToken = default);
}
