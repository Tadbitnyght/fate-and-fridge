using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// The authenticated About payload: the application version and the last applied EF Core
/// migration id.
/// </summary>
/// <remarks>
/// <c>schema_version</c> is the last <em>applied</em> migration. When the
/// database cannot be read it is <c>null</c> rather than a fabricated value
/// and the route still answers <c>200</c>: a version display must never make
/// the app look broken.
/// </remarks>
public sealed record AboutResponse(string AppVersion, string? SchemaVersion);

/// <summary>
/// Version display route. Any authenticated actor may read it — the
/// single-user system actor and members included — so the shell footer and the
/// About section work for everyone. Health (<c>/api/v1/health</c>) keeps its
/// existing unauthenticated contract.
/// </summary>
public static class AboutEndpoints
{
    private const string AboutPath = "/api/v1/about";

    public static IEndpointRouteBuilder MapAboutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(AboutPath, GetAboutAsync)
            .WithTags("Health")
            .RequireAuthenticated();

        return app;
    }

    private static async Task<IResult> GetAboutAsync(
        AppDbContext dbContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var schemaVersion = await ReadSchemaVersionAsync(
                dbContext,
                loggerFactory.CreateLogger("About"),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new AboutResponse(AppInfo.Version, schemaVersion));
    }

    private static async Task<string?> ReadSchemaVersionAsync(
        AppDbContext dbContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var applied = await dbContext.Database
                .GetAppliedMigrationsAsync(cancellationToken)
                .ConfigureAwait(false);

            return applied.LastOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A missing/unreachable database is a degraded display, not a
            // failed request: the version is still useful without it.
            logger.LogWarning(exception, "The applied schema version could not be read for /api/v1/about.");
            return null;
        }
    }
}
