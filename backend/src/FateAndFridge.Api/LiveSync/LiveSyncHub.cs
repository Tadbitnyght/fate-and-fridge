using System.Globalization;
using System.Security.Claims;
using FateAndFridge.Api.Auth;
using Microsoft.AspNetCore.SignalR;

namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// Server-to-client live-sync hub mapped at
/// <see cref="LiveSyncPaths.HubPath"/>. It exposes no client-invokable methods
/// every write stays on the REST API. The connection is gated in
/// <see cref="OnConnectedAsync"/> rather than with <c>RequireAuthorization</c>
/// so single-user mode (no cookie, system actor) can connect while multiuser mode
/// requires a valid session.
/// </summary>
public sealed class LiveSyncHub(
    ICurrentActorProvider actorProvider,
    LiveSyncConnectionRegistry registry,
    ILogger<LiveSyncHub> logger) : Hub
{
    /// <summary>The group every household event is broadcast to.</summary>
    public const string HouseholdGroupName = "household";

    /// <summary>Documented inbound frame cap.</summary>
    public const int MaximumInboundMessageBytes = 32 * 1024;

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        // SignalR can dispatch the hub outside the connect request's
        // IHttpContextAccessor scope (notably for long polling), so fall back to
        // the connection principal, which SignalR captured from the connect
        // request; both paths use the same session claim mapping.
        var actor = await actorProvider.GetCurrentActorAsync(Context.ConnectionAborted)
            ?? SessionActorProvider.FromPrincipal(Context.User);
        if (actor is null)
        {
            Refuse("no valid session");
            return;
        }

        // Fail closed: without an HttpContext the same-origin check cannot run
        // so the connection is refused rather than silently skipping it.
        var httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            Refuse("no request context");
            return;
        }

        if (!IsSameOrigin(httpContext.Request))
        {
            Refuse("cross-origin");
            return;
        }

        var sessionId = ResolveSessionId(Context.User);
        if (!registry.TryRegister(Context.ConnectionId, sessionId, actor.UserId, Context))
        {
            Refuse("connection cap reached");
            return;
        }

        // Close the register-after-switch window (M14 review): a cookie-less
        // connect can read single-user mode, then the household commits the
        // switch to multiuser and DisconnectAllAsync snapshots the registry
        // before this connection registers. The commit precedes the snapshot, so
        // a registration that lands after it re-reads multiuser mode here and is
        // torn down instead of surviving as a system socket the per-broadcast
        // session re-validation would otherwise treat as always valid.
        if (sessionId == LiveSyncConnectionRegistry.SystemSessionId)
        {
            var current = await actorProvider.GetCurrentActorAsync(Context.ConnectionAborted);
            if (current is not { IsSystem: true })
            {
                registry.Unregister(Context.ConnectionId);
                Refuse("single-user mode ended during connect");
                return;
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, HouseholdGroupName);
        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Unregister(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private void Refuse(string reason)
    {
        logger.LogInformation(
            "Refused live-sync connection {ConnectionId}: {Reason}.",
            Context.ConnectionId,
            reason);
        Context.Abort();
    }

    private static long ResolveSessionId(ClaimsPrincipal? user) =>
        long.TryParse(
            user?.FindFirst(SessionAuthenticationDefaults.SessionIdClaim)?.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var sessionId)
            ? sessionId
            : LiveSyncConnectionRegistry.SystemSessionId;

    /// <summary>
    /// Browsers send an <c>Origin</c> header and it must match the request host
    /// a missing Origin from non-browser clients may be allowed (a body-less
    /// same-origin, cookie-authenticated upgrade carries no other cross-origin
    /// signal). Documented decision.
    /// </summary>
    internal static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            return false;
        }

        return string.Equals(originUri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && originUri.Port == EffectivePort(request);
    }

    private static int EffectivePort(HttpRequest request) =>
        request.Host.Port
            ?? (request.IsHttps ? 443 : 80);
}
