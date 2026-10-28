using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Auth;

/// <summary>
/// The identity acting on the current request. <see cref="IsSystem"/> marks the
/// single-user system actor, which owns all
/// data and therefore has full access.
/// </summary>
public sealed record CurrentActor(bool IsSystem, UserRole Role)
{
    /// <summary>
    /// Database id of the acting user; null for the single-user system actor.
    /// Use it for session ownership and permission evaluation.
    /// </summary>
    public long? UserId { get; init; }
}

/// <summary>
/// Authorization seam for the current request. Endpoints depend only on this
/// abstraction so the session work can replace the implementation without
/// touching route handlers.
/// </summary>
/// <remarks>
/// An interim provider
/// (<c>HouseholdAuthModeActorProvider</c>) was replaced by the session-backed
/// <see cref="SessionActorProvider"/>. The full multiuser permission matrix is
/// re-tested against the same endpoints.
/// </remarks>
public interface ICurrentActorProvider
{
    /// <summary>Returns the request actor, or null when the request is unauthenticated.</summary>
    Task<CurrentActor?> GetCurrentActorAsync(CancellationToken cancellationToken = default);
}
