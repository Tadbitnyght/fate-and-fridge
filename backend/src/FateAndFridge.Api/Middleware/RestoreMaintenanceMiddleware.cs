using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Middleware;

/// <summary>
/// Path rules for restore maintenance mode
/// every route is blocked except <c>/api/v1/health</c> and its subroutes, so the
/// container health check keeps succeeding while the database is replaced.
/// </summary>
public static class RestoreMaintenance
{
    private static readonly PathString HealthPrefix = new("/api/v1/health");

    /// <summary>True when the request must be refused while a restore runs.</summary>
    public static bool ShouldBlock(PathString path) => !path.StartsWithSegments(HealthPrefix);
}

/// <summary>
/// Answers <c>503 RESTORE_IN_PROGRESS</c> for every route except health while a
/// restore holds maintenance mode. It runs before authentication so a restore
/// never depends on sessions that are about to be revoked, and so no request
/// reaches the database while it is being replaced.
/// </summary>
public sealed class RestoreMaintenanceMiddleware(RequestDelegate next)
{
    public const string RequiredMessage =
        "The application is restoring a backup and is temporarily unavailable. " +
        "It restarts automatically when the restore finishes.";

    public async Task InvokeAsync(HttpContext context, RestoreState restoreState)
    {
        if (restoreState.IsMaintenanceActive && RestoreMaintenance.ShouldBlock(context.Request.Path))
        {
            var result = ApiResults.Error(
                StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.RestoreInProgress,
                RequiredMessage);
            await result.ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
