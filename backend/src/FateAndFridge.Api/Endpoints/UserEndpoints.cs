using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Reset response: the temporary password is returned exactly once and is
/// never logged or persisted in plaintext (docs/API.md §5).
/// </summary>
public sealed record ResetPasswordResponse(string TemporaryPassword);

/// <summary>
/// User management routes (docs/API.md §5/§6). M1-08 ships the Admin password
/// reset; M1-10 extends this group with CRUD, permissions, and enable/disable.
/// </summary>
public static class UserEndpoints
{
    private const string AuthRequiredMessage = "Authentication is required.";

    private const string ForbiddenMessage = "Only an Admin may reset another user's password.";

    private const string NotFoundMessage = "The requested user does not exist.";

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags("Users");

        // Authorization is enforced in the handler rather than
        // RequireAuthorization() so the single-user system actor (which has no
        // session cookie) takes the same path as an Admin.
        group.MapPost("/{id:long}/reset-password", ResetPasswordAsync);

        return app;
    }

    private static async Task<IResult> ResetPasswordAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        AuthService authService,
        CancellationToken cancellationToken)
    {
        ResponseCache.NoStore(context);

        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            return ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage);
        }

        if (!actor.IsSystem && actor.Role != UserRole.Admin)
        {
            return ApiResults.Error(
                StatusCodes.Status403Forbidden,
                ErrorCodes.Forbidden,
                ForbiddenMessage);
        }

        var result = await authService.ResetPasswordAsync(
            id,
            actor.IsSystem ? null : actor.UserId,
            context.Connection.RemoteIpAddress?.ToString(),
            UserAgent(context),
            cancellationToken);

        return result switch
        {
            ResetPasswordResult.Success success =>
                Results.Ok(new ResetPasswordResponse(success.TemporaryPassword)),
            _ => ApiResults.Error(
                StatusCodes.Status404NotFound,
                ErrorCodes.NotFound,
                NotFoundMessage),
        };
    }

    private static string? UserAgent(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }
}
