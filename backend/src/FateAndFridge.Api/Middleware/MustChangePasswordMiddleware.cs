using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Errors;

namespace FateAndFridge.Api.Middleware;

/// <summary>
/// Path and method rules for the forced-password-change gate (docs/API.md §5).
/// The gate covers every <c>/api/v1</c> route; health and setup routes stay
/// reachable, and a flagged session may still read its session, change the
/// password, and log out.
/// </summary>
public static class MustChangePasswordGate
{
    private static readonly PathString ApiPrefix = new("/api/v1");
    private static readonly PathString HealthPrefix = new("/api/v1/health");
    private static readonly PathString SetupPrefix = new("/api/v1/setup");
    private static readonly PathString SessionPath = new("/api/v1/auth/session");
    private static readonly PathString ChangePasswordPath = new("/api/v1/auth/change-password");
    private static readonly PathString LogoutPath = new("/api/v1/auth/logout");

    /// <summary>Routes a session with <c>must_change_password</c> may still call.</summary>
    public static bool IsAllowed(PathString path, string method) =>
        path.StartsWithSegments(HealthPrefix)
        || path.StartsWithSegments(SetupPrefix)
        || (HttpMethods.IsGet(method) && path.StartsWithSegments(SessionPath))
        || (HttpMethods.IsPost(method) && path.StartsWithSegments(ChangePasswordPath))
        || (HttpMethods.IsPost(method) && path.StartsWithSegments(LogoutPath));

    /// <summary>True when the forced-change gate applies to this route.</summary>
    public static bool ShouldGate(PathString path, string method) =>
        path.StartsWithSegments(ApiPrefix) && !IsAllowed(path, method);
}

/// <summary>
/// Blocks sessions flagged <c>must_change_password</c> with
/// <c>403 PASSWORD_CHANGE_REQUIRED</c> on every gated route. Runs after
/// authentication and CSRF, before authorization and endpoints.
/// </summary>
/// <remarks>
/// The flag is read from the session principal claim, which the session
/// authentication handler rebuilds from the <c>users</c> row on every request.
/// An Admin reset sets the database flag and revokes every session of the
/// target, so a session carrying the old <c>false</c> claim cannot survive the
/// reset; a user who changes the password clears the flag and rotates the
/// current session in the same response. The claim therefore cannot outlive the
/// database flag in a way that matters.
/// </remarks>
public sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    public const string RequiredMessage = "The password must be changed before continuing.";

    public async Task InvokeAsync(HttpContext context)
    {
        if (!MustChangePasswordGate.ShouldGate(context.Request.Path, context.Request.Method)
            || !HasForcedChange(context))
        {
            await next(context);
            return;
        }

        var result = ApiResults.Error(
            StatusCodes.Status403Forbidden,
            ErrorCodes.PasswordChangeRequired,
            RequiredMessage);
        await result.ExecuteAsync(context);
    }

    private static bool HasForcedChange(HttpContext context) =>
        context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(
                identity.AuthenticationType,
                SessionAuthenticationDefaults.Scheme,
                StringComparison.Ordinal)
            && string.Equals(
                identity.FindFirst(SessionAuthenticationDefaults.MustChangePasswordClaim)?.Value,
                "true",
                StringComparison.Ordinal));
}
