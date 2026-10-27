using System.Globalization;
using System.Security.Claims;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// Session-aware <see cref="ICurrentActorProvider"/> (M1-06). Single-user mode
/// always runs as the system actor without requiring a cookie; multiuser mode
/// maps the validated session principal (see
/// <see cref="SessionAuthenticationHandler"/>) to the actor and denies by
/// default when no valid session is present.
/// </summary>
public sealed class SessionActorProvider(
    AppDbContext dbContext,
    IHttpContextAccessor httpContextAccessor) : ICurrentActorProvider
{
    public async Task<CurrentActor?> GetCurrentActorAsync(CancellationToken cancellationToken = default)
    {
        var authMode = await dbContext.HouseholdSettings
            .AsNoTracking()
            .Select(settings => (AuthMode?)settings.AuthMode)
            .SingleOrDefaultAsync(cancellationToken);

        if (authMode == AuthMode.SingleUser)
        {
            return new CurrentActor(IsSystem: true, Role: UserRole.Admin);
        }

        return FromPrincipal(httpContextAccessor.HttpContext?.User);
    }

    /// <summary>
    /// Maps a validated session principal to an actor, or null when it carries
    /// no usable identity or was not authenticated by the session scheme.
    /// Exposed so a network context that runs outside the request's
    /// <see cref="IHttpContextAccessor"/> scope (the SignalR hub, see
    /// <c>LiveSyncHub.OnConnectedAsync</c>) reuses the exact same mapping.
    /// </summary>
    public static CurrentActor? FromPrincipal(ClaimsPrincipal? principal)
    {
        // Mirror CsrfMiddleware.IsSessionAuthenticated / MustChangePasswordMiddleware.HasForcedChange:
        // only the session scheme's own principal is trusted, so another
        // authenticated identity can never be mapped onto a session actor.
        if (principal?.Identity is not { IsAuthenticated: true } identity
            || !string.Equals(
                identity.AuthenticationType,
                SessionAuthenticationDefaults.Scheme,
                StringComparison.Ordinal))
        {
            return null;
        }

        var userIdValue = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var roleValue = principal.FindFirst(ClaimTypes.Role)?.Value;
        if (!long.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            || !EnumTextConverters.TryUserRoleFromText(roleValue, out var role))
        {
            return null;
        }

        return new CurrentActor(IsSystem: false, Role: role) { UserId = userId };
    }
}
