using FateAndFridge.Api.Auth;
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
/// Access is enforced server-side by the M1-09 <c>RequireAdmin()</c> filter.
/// </summary>
public static class UserEndpoints
{
    private const string AuthRequiredMessage = "Authentication is required.";

    private const string NotFoundMessage = "The requested user does not exist.";

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags("Users");

        group.MapPost("/{id:long}/reset-password", ResetPasswordAsync).RequireAdmin();

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
            // RequireAdmin() already rejected unauthenticated requests; keep the
            // defensive envelope so the handler cannot fail open.
            return ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage);
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
