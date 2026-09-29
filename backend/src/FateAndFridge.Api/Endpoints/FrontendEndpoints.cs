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
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsJsonAsync(new { error = "Not Found" });
                    return;
                }

                await context.Response.SendFileAsync(indexFile);
            });

            return;
        }

        app.MapGet("/", () => Results.Ok(new
        {
            app = AppInfo.Name,
            version = AppInfo.Version,
            openapi = "/openapi/v1.json",
            note = "Frontend build not present; use the Vite dev server.",
        }));
    }
}
