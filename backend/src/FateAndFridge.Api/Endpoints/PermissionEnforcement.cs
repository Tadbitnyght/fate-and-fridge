using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Kind of access requirement attached to a route.</summary>
public enum PermissionRequirementKind
{
    /// <summary>Any authenticated actor (including the single-user system actor).</summary>
    Authenticated,

    /// <summary>The single-user system actor or an Admin.</summary>
    Admin,

    /// <summary>An actor whose effective permission set contains a specific key.</summary>
    Permission,
}

/// <summary>Endpoint metadata describing the access requirement of a route.</summary>
public sealed record PermissionRequirement(
    PermissionRequirementKind Kind,
    string? PermissionKey = null);

/// <summary>
/// Endpoint-filter enforcement for permission-protected routes (M1-09).
/// Every attached <see cref="PermissionRequirement"/> is enforced with AND
/// semantics in metadata order; the first failure wins.
/// Unauthenticated requests receive <c>401 AUTH_REQUIRED</c> (with
/// <c>Cache-Control: no-store</c>, matching every auth challenge); authenticated
/// requests failing a requirement receive <c>403 FORBIDDEN</c>. The
/// single-user system actor holds every permission and Admins bypass member
/// permission checks; grants are re-read per request through
/// <see cref="PermissionService"/>, so permission changes apply immediately.
/// </summary>
/// <remarks>
/// Routes must carry at least one requirement, normally attached through
/// <see cref="PermissionEndpointExtensions"/>. Running this filter without any
/// requirement metadata (for example by adding the filter manually) is a
/// server misconfiguration and throws instead of failing open.
/// </remarks>
public sealed class PermissionEndpointFilter(
    ICurrentActorProvider actorProvider,
    PermissionService permissionService) : IEndpointFilter
{
    private const string AuthRequiredMessage = "Authentication is required.";

    private const string ForbiddenMessage =
        "The authenticated actor is missing the required permission.";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        IReadOnlyList<PermissionRequirement> requirements =
            context.HttpContext.GetEndpoint()?
                .Metadata.GetOrderedMetadata<PermissionRequirement>()
            ?? [];

        if (requirements.Count == 0)
        {
            throw new InvalidOperationException(
                $"{nameof(PermissionEndpointFilter)} requires at least one " +
                $"{nameof(PermissionRequirement)} on the endpoint; attach requirements with " +
                $"{nameof(PermissionEndpointExtensions)} extension methods " +
                $"(RequireAuthenticated/RequireAdmin/RequirePermission).");
        }

        var actor = await actorProvider.GetCurrentActorAsync(context.HttpContext.RequestAborted);
        if (actor is null)
        {
            ResponseCache.NoStore(context.HttpContext);
            return ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage);
        }

        foreach (var requirement in requirements)
        {
            var allowed = requirement.Kind switch
            {
                PermissionRequirementKind.Authenticated => true,
                PermissionRequirementKind.Admin => actor.IsSystem || actor.Role == UserRole.Admin,
                PermissionRequirementKind.Permission => await permissionService.HasPermissionAsync(
                    actor,
                    requirement.PermissionKey!,
                    context.HttpContext.RequestAborted),
                _ => false,
            };

            if (!allowed)
            {
                return ApiResults.Error(
                    StatusCodes.Status403Forbidden,
                    ErrorCodes.Forbidden,
                    ForbiddenMessage);
            }
        }

        return await next(context);
    }
}

/// <summary>
/// Route-builder helpers that attach the M1-09 access requirement and the
/// enforcing filter to an endpoint. Enforcement is server-side and independent
/// of the frontend.
/// </summary>
public static class PermissionEndpointExtensions
{
    /// <summary>Allows any authenticated actor (system actor included).</summary>
    public static RouteHandlerBuilder RequireAuthenticated(this RouteHandlerBuilder builder) =>
        builder
            .AddEndpointFilter<PermissionEndpointFilter>()
            .WithMetadata(new PermissionRequirement(PermissionRequirementKind.Authenticated));

    /// <summary>Allows the single-user system actor or an Admin.</summary>
    public static RouteHandlerBuilder RequireAdmin(this RouteHandlerBuilder builder) =>
        builder
            .AddEndpointFilter<PermissionEndpointFilter>()
            .WithMetadata(new PermissionRequirement(PermissionRequirementKind.Admin));

    /// <summary>
    /// Allows actors whose effective permission set contains
    /// <paramref name="permissionKey"/> (Admins and the system actor always do).
    /// Unknown keys fail fast at startup.
    /// </summary>
    public static RouteHandlerBuilder RequirePermission(
        this RouteHandlerBuilder builder,
        string permissionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);
        if (!PermissionKeys.IsKnown(permissionKey))
        {
            throw new ArgumentException(
                $"'{permissionKey}' is not a known permission key.",
                nameof(permissionKey));
        }

        return builder
            .AddEndpointFilter<PermissionEndpointFilter>()
            .WithMetadata(new PermissionRequirement(PermissionRequirementKind.Permission, permissionKey));
    }
}
