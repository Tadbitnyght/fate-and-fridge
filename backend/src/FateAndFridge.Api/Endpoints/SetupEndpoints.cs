using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Setup routes (docs/API.md §21). Public while setup is incomplete.</summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/setup").WithTags("Setup");

        group.MapGet("/status", async (SetupStateService setupState, CancellationToken cancellationToken) =>
            Results.Ok(await setupState.GetStatusAsync(cancellationToken)));

        return app;
    }
}
