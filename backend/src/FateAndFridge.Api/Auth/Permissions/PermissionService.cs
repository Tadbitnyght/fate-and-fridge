namespace FateAndFridge.Api.Auth.Permissions;

/// <summary>
/// Effective-permission service for the current actor: the single
/// source of truth shared by <c>GET /auth/session</c> and route enforcement.
/// Reads grants fresh from <see cref="IPermissionGrantReader"/> on every call —
/// no cross-request caching — so permission changes apply on the next request.
/// </summary>
public sealed class PermissionService(IPermissionGrantReader grantReader)
{
    /// <summary>
    /// Effective permission keys for <paramref name="actor"/>. The system actor
    /// and Admins hold all keys without a database read.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEffectiveKeysAsync(
        CurrentActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (actor.IsSystem || actor.Role == Domain.UserRole.Admin)
        {
            return PermissionEvaluator.For(actor);
        }

        if (actor.UserId is not { } userId)
        {
            return [];
        }

        var grantedKeys = await grantReader.ReadGrantedKeysAsync(userId, cancellationToken);
        return PermissionEvaluator.For(actor, grantedKeys);
    }

    /// <summary>
    /// True when <paramref name="actor"/> effectively holds
    /// <paramref name="permissionKey"/>. Unknown keys never match.
    /// </summary>
    public async Task<bool> HasPermissionAsync(
        CurrentActor actor,
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!PermissionKeys.IsKnown(permissionKey))
        {
            return false;
        }

        var effectiveKeys = await GetEffectiveKeysAsync(actor, cancellationToken);
        return effectiveKeys.Contains(permissionKey, StringComparer.Ordinal);
    }
}
