using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Auth;

/// <summary>Names and claim types for the opaque session cookie.</summary>
public static class SessionAuthenticationDefaults
{
    /// <summary>Authentication scheme name; also the default scheme.</summary>
    public const string Scheme = "FateAndFridgeSession";

    /// <summary>Opaque session cookie name (docs/SECURITY.md §2).</summary>
    public const string CookieName = "faf_session";

    /// <summary>Claim carrying the forced-password-change flag (M1-08).</summary>
    public const string MustChangePasswordClaim = "must_change_password";

    /// <summary>
    /// Claim carrying the database id of the resolved session, used by logout
    /// to revoke exactly the current session (M1-07).
    /// </summary>
    public const string SessionIdClaim = "session_id";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key holding the resolved session's
    /// stored token hash, used to bind antiforgery tokens to the session.
    /// </summary>
    public const string SessionTokenHashItemKey = "faf.session_token_hash";
}

/// <summary>
/// Writes and deletes the session cookie with the approved flags: HttpOnly
/// (not readable by JavaScript), SameSite=Lax, Path=/, and Secure whenever the
/// request is HTTPS (docs/SECURITY.md §2). The cookie value is the raw opaque
/// session token.
/// </summary>
public static class SessionCookie
{
    /// <summary>Creates the cookie options for the current request.</summary>
    public static CookieOptions CreateOptions(HttpRequest request) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = request.IsHttps,
        Path = "/",
    };

    /// <summary>
    /// Sets the session cookie on the response (used by login, M1-07). Pass
    /// <paramref name="expires"/> to write a persistent cookie; omitting it
    /// writes a browser-session cookie.
    /// </summary>
    public static void Append(HttpResponse response, string rawToken, DateTimeOffset? expires = null)
    {
        var options = CreateOptions(response.HttpContext.Request);
        options.Expires = expires;
        response.Cookies.Append(SessionAuthenticationDefaults.CookieName, rawToken, options);
    }

    /// <summary>Expires the session cookie on the response.</summary>
    public static void Delete(HttpResponse response) =>
        response.Cookies.Delete(
            SessionAuthenticationDefaults.CookieName,
            CreateOptions(response.HttpContext.Request));
}

/// <summary>Builds the request principal for a resolved session user.</summary>
internal static class SessionClaims
{
    public static ClaimsPrincipal Create(User user, long sessionId)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, EnumTextConverters.UserRoleToText(user.Role)),
                new Claim(
                    SessionAuthenticationDefaults.MustChangePasswordClaim,
                    user.MustChangePassword ? "true" : "false"),
                new Claim(
                    SessionAuthenticationDefaults.SessionIdClaim,
                    sessionId.ToString(CultureInfo.InvariantCulture)),
            ],
            authenticationType: SessionAuthenticationDefaults.Scheme);

        return new ClaimsPrincipal(identity);
    }
}

/// <summary>
/// Authentication scheme for the opaque database-backed session cookie. The
/// cookie value is the raw token; the handler resolves it through
/// <see cref="SessionService"/> and builds the request principal. Missing
/// cookies are simply unauthenticated; invalid, expired, or revoked cookies are
/// rejected and deleted without throwing (stable behavior, no 500).
/// </summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly SessionService _sessionService;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        SessionService sessionService)
        : base(options, logger, encoder)
    {
        _sessionService = sessionService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionAuthenticationDefaults.CookieName, out var rawToken)
            || string.IsNullOrWhiteSpace(rawToken))
        {
            return AuthenticateResult.NoResult();
        }

        var resolved = await _sessionService.ResolveAsync(rawToken, Context.RequestAborted);
        if (resolved is null)
        {
            SessionCookie.Delete(Response);
            return AuthenticateResult.Fail("The session cookie is invalid, expired, or revoked.");
        }

        // Bind antiforgery request tokens to this session (see
        // SessionAntiforgeryDataProvider): rotation replaces the stored hash,
        // so tokens minted before rotation stop validating.
        Context.Items[SessionAuthenticationDefaults.SessionTokenHashItemKey] =
            Convert.ToHexString(resolved.Session.TokenHash);

        return AuthenticateResult.Success(
            new AuthenticationTicket(SessionClaims.Create(resolved.User, resolved.Session.Id), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Challenge responses (for example an unauthenticated
        // POST /auth/change-password) must not be cached, matching every other
        // auth response (docs/API.md §5).
        Response.Headers.CacheControl = "no-store";

        // Routes using RequireAuthorization() get the same stable envelope as
        // endpoints that check ICurrentActorProvider themselves.
        var result = ApiResults.Error(
            StatusCodes.Status401Unauthorized,
            ErrorCodes.AuthRequired,
            "Authentication is required.");
        await result.ExecuteAsync(Context);
    }
}
