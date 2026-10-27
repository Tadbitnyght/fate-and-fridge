using FateAndFridge.Api.Errors;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Serves the built frontend when present (Docker image) and falls back to a
/// small JSON index in development, where the Vite dev server owns the UI.
/// Unknown non-API paths fall through to index.html for client-side routing.
/// </summary>
public static class FrontendEndpoints
{
    public static void MapFrontend(this WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath
            ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        var indexFile = Path.Combine(webRoot, "index.html");

        if (File.Exists(indexFile))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.MapFallback(async context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    // Unknown API paths use the shared error envelope (docs/API.md §4).
                    await ApiResults.Error(
                        StatusCodes.Status404NotFound,
                        ErrorCodes.NotFound,
                        "The requested endpoint does not exist.").ExecuteAsync(context);
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(indexFile);
            });

            return;
        }

        app.MapGet("/", () => Results.Ok(new
        {
            app = AppInfo.Name,
            version = AppInfo.Version,
            note = "Frontend build not present; use the Vite dev server.",
        }));
    }
}
