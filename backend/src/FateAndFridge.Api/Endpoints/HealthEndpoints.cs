using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Health and readiness endpoints.</summary>
public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").WithTags("Health");

        group.MapGet("/health", (IHostEnvironment environment) =>
            Results.Ok(new HealthResponse("ok", AppInfo.Name, AppInfo.Version, environment.EnvironmentName)));

        group.MapGet("/health/db", async (AppDbContext dbContext, ILoggerFactory loggerFactory) =>
        {
            var connected = await CanConnectAsync(dbContext, loggerFactory.CreateLogger("Health"));
            return Results.Ok(connected
                ? new DatabaseHealthResponse("ok", "connected")
                : new DatabaseHealthResponse("degraded", "unavailable"));
        });

        return app;
    }

    private static async Task<bool> CanConnectAsync(AppDbContext dbContext, ILogger logger)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Database health check failed");
            return false;
        }
    }
}

/// <summary>Application liveness payload.</summary>
public sealed record HealthResponse(string Status, string App, string Version, string Environment);

/// <summary>Database connectivity payload.</summary>
public sealed record DatabaseHealthResponse(string Status, string Database);
