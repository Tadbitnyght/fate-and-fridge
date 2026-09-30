using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Middleware;

/// <summary>
/// Path rules for pre-setup gating (docs/API.md §21): every <c>/api/v1</c>
/// route is gated except <c>/health</c> (and its subroutes) and
/// <c>/setup/*</c>. Non-API paths (frontend, OpenAPI documents) are never
/// gated.
/// </summary>
public static class SetupGating
{
    private static readonly PathString ApiPrefix = new("/api/v1");
    private static readonly PathString HealthPrefix = new("/api/v1/health");
    private static readonly PathString SetupPrefix = new("/api/v1/setup");

    /// <summary>Returns true when the request must wait for setup completion.</summary>
    public static bool ShouldGate(PathString path) =>
        path.StartsWithSegments(ApiPrefix)
        && !path.StartsWithSegments(HealthPrefix)
        && !path.StartsWithSegments(SetupPrefix);
}

/// <summary>
/// Blocks gated API routes with <c>409 SETUP_REQUIRED</c> until setup
/// completes. Setup state is read from the database per request, so completing
/// setup takes effect without restarting the application.
/// </summary>
public sealed class SetupGatingMiddleware(RequestDelegate next)
{
    public const string RequiredMessage =
        "Setup is not complete; only /health and /setup/* are callable.";

    public async Task InvokeAsync(HttpContext context, SetupStateService setupState)
    {
        if (SetupGating.ShouldGate(context.Request.Path)
            && !await setupState.IsSetupCompletedAsync(context.RequestAborted))
        {
            var result = ApiResults.Error(
                StatusCodes.Status409Conflict,
                ErrorCodes.SetupRequired,
                RequiredMessage);
            await result.ExecuteAsync(context);
            return;
        }

        await next(context);
    }
}
