using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// The identity acting on the current request. <see cref="IsSystem"/> marks the
/// single-user system actor (docs/MILESTONES.md M1 decisions), which owns all
/// data and therefore has full access.
/// </summary>
public sealed record CurrentActor(bool IsSystem, UserRole Role);

/// <summary>
/// Authorization seam for the current request. Endpoints depend only on this
/// abstraction so the M1 session work can replace the implementation without
/// touching route handlers.
/// </summary>
/// <remarks>
/// M1-03 ships the interim implementation
/// (<see cref="HouseholdAuthModeActorProvider"/>). M1-06/M1-07 replace it with
/// the session-backed provider, and M1-09 re-tests the full multiuser
/// permission matrix against the same endpoints.
/// </remarks>
public interface ICurrentActorProvider
{
    /// <summary>Returns the request actor, or null when the request is unauthenticated.</summary>
    Task<CurrentActor?> GetCurrentActorAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interim actor provider (M1-03). In single-user mode every request runs as
/// the system actor with full access. In multiuser mode no session mechanism
/// exists yet, so every request is unauthenticated and therefore denied by
/// default rather than accidentally granted.
/// </summary>
public sealed class HouseholdAuthModeActorProvider(AppDbContext dbContext) : ICurrentActorProvider
{
    public async Task<CurrentActor?> GetCurrentActorAsync(CancellationToken cancellationToken = default)
    {
        var authMode = await dbContext.HouseholdSettings
            .AsNoTracking()
            .Select(settings => (AuthMode?)settings.AuthMode)
            .SingleOrDefaultAsync(cancellationToken);

        return authMode switch
        {
            AuthMode.SingleUser => new CurrentActor(IsSystem: true, Role: UserRole.Admin),
            _ => null,
        };
    }
}
