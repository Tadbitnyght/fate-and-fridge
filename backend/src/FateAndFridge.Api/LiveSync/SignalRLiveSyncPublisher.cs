using Microsoft.AspNetCore.SignalR;

namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// SignalR-backed <see cref="ILiveSyncPublisher"/> (docs/M9_PLAN.md §2.1).
/// Broadcasts to the household group through the single <c>event</c> client
/// method; <see cref="PublishSessionRevokedAsync"/> targets one session's
/// connections and aborts them. Delivery is best-effort: failures are logged and
/// swallowed and the methods are no-ops when nothing is connected, so a
/// notification can never fail a committed write (M9-02…M9-06 call these after
/// commit).
/// </summary>
public sealed class SignalRLiveSyncPublisher(
    IHubContext<LiveSyncHub> hubContext,
    LiveSyncConnectionRegistry registry,
    ILogger<SignalRLiveSyncPublisher> logger) : ILiveSyncPublisher
{
    /// <summary>The single client method every event is delivered on.</summary>
    public const string ClientMethod = "event";

    /// <inheritdoc />
    public async Task PublishAsync(LiveSyncEvent evt, CancellationToken cancellationToken = default)
    {
        if (registry.Count == 0)
        {
            return;
        }

        try
        {
            await hubContext.Clients
                .Group(LiveSyncHub.HouseholdGroupName)
                .SendAsync(ClientMethod, evt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled; the notification is best-effort.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Live-sync broadcast of {EventType} failed.", evt.Type);
        }
    }

    /// <inheritdoc />
    public async Task PublishSessionRevokedAsync(long sessionId, CancellationToken cancellationToken = default)
    {
        var connections = registry.GetConnections(sessionId);
        if (connections.Count == 0)
        {
            return;
        }

        var evt = LiveSyncEvent.Create(LiveSyncEventType.SessionRevoked);
        try
        {
            await hubContext.Clients
                .Clients(connections.Select(connection => connection.ConnectionId).ToList())
                .SendAsync(ClientMethod, evt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled; the notification is best-effort.
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Live-sync session.revoked delivery for session {SessionId} failed.",
                sessionId);
        }
        finally
        {
            foreach (var connection in connections)
            {
                connection.Context.Abort();
            }
        }
    }
}
