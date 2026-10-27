using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Middleware;

/// <summary>
/// Resolves the client address and scheme behind a trusted reverse proxy
/// (docs/SECURITY.md §5, docs/API.md §21). Forwarded headers are honored only
/// when the immediate socket peer is inside the configured trusted-proxy
/// ranges; otherwise they are ignored entirely (spoof protection). Runs before
/// authentication so <c>Secure</c>-cookie decisions and the allowlist use the
/// resolved client. Skipped for <c>/api/v1/health/*</c> so liveness never
/// depends on the database.
/// </summary>
public sealed class ClientAddressMiddleware(RequestDelegate next)
{
    private static readonly PathString HealthPrefix = new("/api/v1/health");

    public async Task InvokeAsync(HttpContext context, SecuritySettingsService securitySettings)
    {
        if (context.Request.Path.StartsWithSegments(HealthPrefix))
        {
            await next(context);
            return;
        }

        var settings = await securitySettings.GetAsync(context.RequestAborted);
        if (settings.TrustedProxies.Count > 0)
        {
            if (ClientAddress.IsForwardedHttps(
                    context.Connection.RemoteIpAddress,
                    settings.TrustedProxies,
                    context.Request.Headers["X-Forwarded-Proto"]))
            {
                // TLS terminated at the proxy: make cookie Secure flags work.
                context.Request.Scheme = "https";
            }

            var resolved = ClientAddress.ResolveClientAddress(
                context.Connection.RemoteIpAddress,
                settings.TrustedProxies,
                context.Request.Headers["X-Forwarded-For"]);
            if (resolved is not null)
            {
                context.Connection.RemoteIpAddress = resolved;
            }
        }

        await next(context);
    }
}
