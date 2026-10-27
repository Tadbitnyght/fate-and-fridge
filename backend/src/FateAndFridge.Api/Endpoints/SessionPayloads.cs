using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Builds the session payload shared by login, <c>GET /auth/session</c>, and
/// setup completion (docs/API.md §5), so every sign-in response reports the
/// same user, role, effective permissions, and forced-change flag.
/// </summary>
public static class SessionPayloads
{
    public static async Task<AuthSessionResponse> ForUserAsync(
        PermissionService permissionService,
        User user,
        CancellationToken cancellationToken)
    {
        var actor = new CurrentActor(IsSystem: false, Role: user.Role) { UserId = user.Id };

        return new AuthSessionResponse(
            Authenticated: true,
            AuthMode: AuthSessionResponse.MultiuserAuthMode,
            User: new AuthUserResponse(
                user.Id,
                user.Username,
                EnumTextConverters.UserRoleToText(user.Role)),
            Permissions: await permissionService.GetEffectiveKeysAsync(actor, cancellationToken),
            MustChangePassword: user.MustChangePassword);
    }
}
