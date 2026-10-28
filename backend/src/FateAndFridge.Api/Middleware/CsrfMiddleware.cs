using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.LiveSync;
using Microsoft.AspNetCore.Antiforgery;

namespace FateAndFridge.Api.Middleware;

/// <summary>
/// Antiforgery protection for authenticated session requests (docs/SECURITY.md
/// §2/§15, docs/API.md §5). Authenticated unsafe methods must present the
/// antiforgery request token in the <c>X-CSRF-TOKEN</c> header; a missing or
/// invalid token fails with <c>403 CSRF_VALIDATION_FAILED</c>. Safe methods
/// (GET/HEAD/OPTIONS) and unauthenticated requests (including stale or invalid
/// session cookies) are not validated and fall through to normal
/// authentication/authorization handling. Every response to an authenticated
/// request exposes a fresh request token in the same header for the frontend to
/// echo back.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    /// <summary>Request/response header carrying the antiforgery request token.</summary>
    public const string HeaderName = "X-CSRF-TOKEN";

    private const string FailedMessage = "The antiforgery token is missing or invalid.";

    /// <summary>
    /// Public setup completion route (docs/API.md §21). Repeat calls must
    /// observe <c>409 SETUP_ALREADY_COMPLETED</c> even when the client still
    /// carries the session cookie issued by the completion response; the
    /// endpoint performs no authenticated state change once setup is complete.
    /// </summary>
    private static readonly PathString SetupCompletePath = new("/api/v1/setup/complete");

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        // The live-sync hub is exempt from antiforgery validation. The
        // SPA connects with skipNegotiation over WebSockets only, so the connect
        // request is a body-less, same-origin, cookie-authenticated upgrade with
        // no state-changing form to forge; the exemption also keeps a future
        // /negotiate POST working. IpAllowlist, SetupGating, and
        // MustChangePassword still apply to the connect request.
        if (LiveSyncPaths.IsHubPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (!IsSessionAuthenticated(context))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.StartsWithSegments(SetupCompletePath))
        {
            await next(context);
            return;
        }

        context.Response.Headers[HeaderName] = antiforgery.GetAndStoreTokens(context).RequestToken;

        if (IsUnsafeMethod(context.Request.Method))
        {
            var failed = await ValidateAsync(context, antiforgery);
            if (failed is not null)
            {
                await failed.ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    private static async Task<IResult?> ValidateAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var requestToken = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(requestToken))
        {
            return Failed();
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Failed();
        }
    }

    private static bool IsUnsafeMethod(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    /// <summary>
    /// True when the session authentication scheme produced an authenticated
    /// principal for this request (decision: CSRF follows the session, not the
    /// mere presence of a cookie).
    /// </summary>
    private static bool IsSessionAuthenticated(HttpContext context) =>
        context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                SessionAuthenticationDefaults.Scheme,
                StringComparison.Ordinal));

    private static IResult Failed() =>
        ApiResults.Error(
            StatusCodes.Status403Forbidden,
            ErrorCodes.CsrfValidationFailed,
            FailedMessage);
}
