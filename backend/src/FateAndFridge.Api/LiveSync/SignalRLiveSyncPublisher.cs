using FateAndFridge.Api.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// SignalR-backed <see cref="ILiveSyncPublisher"/> (docs/M9_PLAN.md §2.1).
/// Broadcasts to the household through the single <c>event</c> client method;
/// the revocation methods target one session's (or one user's) connections and
/// abort them. Delivery is best-effort: failures are logged and swallowed and
/// the methods are no-ops when nothing is connected, so a notification can never
/// fail a committed write (M9-02…M9-06 call these after commit).
/// </summary>
/// <remarks>
/// Every broadcast re-validates the target connections' sessions against
/// <c>user_sessions</c> before delivering (docs/M9_PLAN.md §1.13): a session
/// revoked or expired out-of-band receives <c>session.revoked</c>, is aborted,
/// and is excluded from the broadcast. If that re-validation cannot be completed
/// the broadcast fails closed: the event is skipped for every connection rather
/// than delivered to unvalidated sockets, and delivery resumes on the next event
/// or client reconnect. The publisher is a singleton, so it creates a fresh
/// <see cref="IServiceScope"/> per broadcast for the scoped
/// <see cref="AppDbContext"/>/<see cref="TimeProvider"/>.
/// </remarks>
public sealed class SignalRLiveSyncPublisher(
    IHubContext<LiveSyncHub> hubContext,
    LiveSyncConnectionRegistry registry,
    IServiceScopeFactory scopeFactory,
    ILogger<SignalRLiveSyncPublisher> logger) : ILiveSyncPublisher
{
    /// <summary>The single client method every event is delivered on.</summary>
    public const string ClientMethod = "event";

    /// <inheritdoc />
    public async Task PublishAsync(LiveSyncEvent evt, CancellationToken cancellationToken = default)
    {
        var connections = registry.GetAll();
        if (connections.Count == 0)
        {
            return;
        }

        try
        {
            // Re-validate before delivering: invalid connections are told and
            // aborted here and excluded from the broadcast, so a revoked session
            // can never keep receiving household events. If validation cannot be
            // completed the partition is empty, so nothing is delivered.
            var (valid, invalid) = await PartitionBySessionValidityAsync(connections, cancellationToken);
            await RevokeConnectionsAsync(invalid, cancellationToken);

            if (valid.Count == 0)
            {
                return;
            }

            await hubContext.Clients
                .Clients(valid.Select(connection => connection.ConnectionId).ToList())
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
    public Task PublishSessionRevokedAsync(long sessionId, CancellationToken cancellationToken = default) =>
        RevokeConnectionsAsync(registry.GetConnections(sessionId), cancellationToken);

    /// <inheritdoc />
    public Task PublishSessionRevokedForUserAsync(
        long userId,
        long? exceptSessionId = null,
        CancellationToken cancellationToken = default) =>
        RevokeConnectionsAsync(registry.GetConnectionsForUser(userId, exceptSessionId), cancellationToken);

    /// <summary>
    /// Sends <c>session.revoked</c> to <paramref name="connections"/> and then
    /// aborts each one. Best-effort: a delivery failure is logged and the abort
    /// still runs.
    /// </summary>
    private async Task RevokeConnectionsAsync(
        IReadOnlyList<LiveSyncConnection> connections,
        CancellationToken cancellationToken)
    {
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
                "Live-sync session.revoked delivery for {ConnectionCount} connection(s) failed.",
                connections.Count);
        }
        finally
        {
            foreach (var connection in connections)
            {
                connection.Context.Abort();
            }
        }
    }

    /// <summary>
    /// Splits the registered connections into still-valid and no-longer-valid
    /// sessions. System-actor connections (single-user mode, session id
    /// <see cref="LiveSyncConnectionRegistry.SystemSessionId"/>) have no database
    /// session and skip validation. A non-system session is valid when its
    /// <c>user_sessions</c> row is unrevoked, unexpired, and owned by an active
    /// user, mirroring <c>SessionService.ResolveAsync</c>.
    /// </summary>
    private async Task<(List<LiveSyncConnection> Valid, List<LiveSyncConnection> Invalid)>
        PartitionBySessionValidityAsync(
            IReadOnlyList<LiveSyncConnection> connections,
            CancellationToken cancellationToken)
    {
        var valid = new List<LiveSyncConnection>(connections.Count);
        var invalid = new List<LiveSyncConnection>();

        var sessionIds = connections
            .Where(connection => connection.SessionId != LiveSyncConnectionRegistry.SystemSessionId)
            .Select(connection => connection.SessionId)
            .Distinct()
            .ToList();
        if (sessionIds.Count == 0)
        {
            // Only system-actor connections are registered.
            valid.AddRange(connections);
            return (valid, invalid);
        }

        HashSet<long> liveSessionIds;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
            liveSessionIds = (await dbContext.UserSessions
                    .AsNoTracking()
                    .Where(session => sessionIds.Contains(session.Id)
                        && session.RevokedAt == null
                        && session.IdleExpiresAt > now
                        && session.AbsoluteExpiresAt > now
                        && dbContext.Users.Any(user => user.Id == session.UserId && user.IsActive))
                    .Select(session => session.Id)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Fail closed (docs/M9_PLAN.md §1.13): a validation read failure
            // must never deliver household events to connections whose sessions
            // could not be re-validated. Skip this event for every connection;
            // delivery self-heals on the next event or client reconnect once the
            // read succeeds. The connections are not aborted — a read failure is
            // not proof of revocation.
            logger.LogWarning(
                exception,
                "Live-sync session re-validation failed; skipping the event for {ConnectionCount} connection(s).",
                connections.Count);
            return (valid, invalid);
        }

        foreach (var connection in connections)
        {
            if (connection.SessionId == LiveSyncConnectionRegistry.SystemSessionId
                || liveSessionIds.Contains(connection.SessionId))
            {
                valid.Add(connection);
            }
            else
            {
                invalid.Add(connection);
            }
        }

        return (valid, invalid);
    }
}
