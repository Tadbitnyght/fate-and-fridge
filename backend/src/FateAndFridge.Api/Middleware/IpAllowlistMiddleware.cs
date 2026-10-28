using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Middleware;

/// <summary>
/// App-wide IP allowlist (docs/SECURITY.md §5, docs/API.md §21). When enabled
/// only loopback and clients covered by the configured ranges may reach any
/// route except <c>/api/v1/health/*</c>; blocked clients receive
/// <c>403 IP_NOT_ALLOWED</c>. Runs before authentication and setup gating so a
/// blocked client is rejected early and cannot learn setup state. Settings are
/// read per request through the short-TTL cache and fail open when the database
/// is unreadable (see <see cref="SecuritySettingsService"/>).
/// </summary>
public sealed class IpAllowlistMiddleware(RequestDelegate next)
{
    private static readonly PathString HealthPrefix = new("/api/v1/health");

    private const string BlockedMessage = "The client address is not allowed.";

    public async Task InvokeAsync(HttpContext context, SecuritySettingsService securitySettings)
    {
        if (context.Request.Path.StartsWithSegments(HealthPrefix))
        {
            await next(context);
            return;
        }

        var settings = await securitySettings.GetAsync(context.RequestAborted);
        if (!settings.IpAllowlistEnabled)
        {
            await next(context);
            return;
        }

        var client = ClientAddress.Normalize(context.Connection.RemoteIpAddress);
        if (client is null
            || ClientAddress.IsLoopback(client)
            || ClientAddress.IsInAnyRange(client, settings.IpAllowlistRanges))
        {
            await next(context);
            return;
        }

        var result = ApiResults.Error(
            StatusCodes.Status403Forbidden,
            ErrorCodes.IpNotAllowed,
            BlockedMessage);
        await result.ExecuteAsync(context);
    }
}
