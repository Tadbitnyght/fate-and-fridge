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

        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
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
