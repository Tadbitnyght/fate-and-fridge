using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Endpoints;

/// <summary>User object of the login and session payloads (docs/API.md §5).</summary>
public sealed record AuthUserResponse(long Id, string Username, string Role);

/// <summary>
/// Session snapshot shared by <c>POST /auth/login</c> (200) and
/// <c>GET /auth/session</c> (docs/API.md §5). Single-user mode reports the
/// system identity with <see cref="User"/> null and all permissions.
/// </summary>
public sealed record AuthSessionResponse(
    bool Authenticated,
    string AuthMode,
    AuthUserResponse? User,
    IReadOnlyList<string> Permissions,
    bool MustChangePassword)
{
    public const string MultiuserAuthMode = "multiuser";

    public const string SingleUserAuthMode = "single_user";

    /// <summary>Single-user response: system actor, no user row, all permissions.</summary>
    public static AuthSessionResponse SingleUser() =>
        new(
            Authenticated: true,
            AuthMode: SingleUserAuthMode,
            User: null,
            Permissions: PermissionKeys.All,
            MustChangePassword: false);
}

/// <summary>
/// Authentication routes (docs/API.md §5): login, logout, and the session
/// snapshot.
/// </summary>
/// <remarks>
/// Login CSRF note: <c>POST /auth/login</c> is unauthenticated and therefore
/// exempt from the authenticated-only antiforgery check by design (M1-06
/// decision). Requiring a JSON body and using SameSite=Lax cookies are the
/// interim cross-site mitigations; M1-15 explicitly revisits login CSRF.
/// </remarks>
public static class AuthEndpoints
{
    private const string AuthRequiredMessage = "Authentication is required.";

    private const string InvalidCredentialsMessage = "The username or password is incorrect.";

    private const string AccountLockedMessage = "The account is temporarily locked.";

    private const string RateLimitedMessage = "Too many failed login attempts. Try again later.";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        // Unauthenticated by contract; validation failures are 400, credential
        // failures are the generic 401 below.
        group.MapPost("/login", LoginAsync);

        // The CSRF middleware requires X-CSRF-TOKEN on this authenticated
        // unsafe request; RequireAuthorization() yields 401 AUTH_REQUIRED
        // without a valid session.
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();

        group.MapGet("/session", GetSessionAsync);

        // Allowed by the forced-change gate; rotates the current session.
        group.MapPost("/change-password", ChangePasswordAsync).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        AuthService authService,
        IAntiforgery antiforgery,
        PermissionService permissionService,
        CancellationToken cancellationToken)
    {
        ResponseCache.NoStore(context);

        var (request, validationError) = await ReadLoginRequestAsync(context, cancellationToken);
        if (request is null)
        {
            return validationError!;
        }

        var result = await authService.LoginAsync(
            request.Username,
            request.Password,
            RemoteIpAddress(context),
            UserAgent(context),
            cancellationToken);

        return result switch
        {
            LoginResult.Success success =>
                await LoginSuccessAsync(context, permissionService, antiforgery, success, cancellationToken),
            LoginResult.AccountLocked locked => ApiResults.Error(
                StatusCodes.Status423Locked,
                ErrorCodes.AccountLocked,
                AccountLockedMessage,
                new Dictionary<string, object?> { ["locked_until"] = locked.LockedUntil }),
            LoginResult.RateLimited rateLimited => RateLimited(context, rateLimited),
            _ => InvalidCredentials(),
        };
    }

    private static IResult RateLimited(HttpContext context, LoginResult.RateLimited rateLimited)
    {
        // Retry-After is whole seconds per RFC 9110; round up so a sub-second
        // remainder never advertises "0".
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(rateLimited.RetryAfter.TotalSeconds));
        context.Response.Headers["Retry-After"] =
            retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

        return ApiResults.Error(
            StatusCodes.Status429TooManyRequests,
            ErrorCodes.RateLimited,
            RateLimitedMessage);
    }

    private static async Task<IResult> LoginSuccessAsync(
        HttpContext context,
        PermissionService permissionService,
        IAntiforgery antiforgery,
        LoginResult.Success success,
        CancellationToken cancellationToken)
    {
        // Shared with setup completion: persistent cookie, session principal,
        // and a fresh antiforgery pair bound to the new session.
        SessionSignIn.Apply(context, success.User, success.Session, success.RawToken, antiforgery);

        return Results.Ok(await SessionPayloads.ForUserAsync(permissionService, success.User, cancellationToken));
    }

    /// <summary>
    /// Self-service password change (docs/API.md §5). The current password must
    /// verify; the new password follows the 12–128 character policy; success
    /// rotates the session cookie and returns <c>204</c>.
    /// </summary>
    private static async Task<IResult> ChangePasswordAsync(
        HttpContext context,
        AuthService authService,
        IAntiforgery antiforgery,
        IOptions<AntiforgeryOptions> antiforgeryOptions,
        CancellationToken cancellationToken)
    {
        ResponseCache.NoStore(context);

        var (request, validationError) = await ReadChangePasswordRequestAsync(context, cancellationToken);
        if (request is null)
        {
            return validationError!;
        }

        var userIdValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var sessionIdValue = context.User.FindFirstValue(SessionAuthenticationDefaults.SessionIdClaim);
        if (!long.TryParse(userIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            || !long.TryParse(sessionIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionId))
        {
            return AuthRequired();
        }

        var result = await authService.ChangePasswordAsync(
            userId,
            sessionId,
            request.CurrentPassword,
            request.NewPassword,
            RemoteIpAddress(context),
            UserAgent(context),
            cancellationToken);

        return result switch
        {
            ChangePasswordResult.Success success =>
                ChangePasswordSucceeded(context, antiforgery, antiforgeryOptions, success),
            ChangePasswordResult.InvalidCredentials => InvalidCredentials(),
            ChangePasswordResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            _ => AuthRequired(),
        };
    }

    private static IResult ChangePasswordSucceeded(
        HttpContext context,
        IAntiforgery antiforgery,
        IOptions<AntiforgeryOptions> antiforgeryOptions,
        ChangePasswordResult.Success success)
    {
        SessionCookie.Append(context.Response, success.RotatedToken, success.CookieExpiresAt);

        // Rotation replaced the stored token hash, so both the cookie token
        // decision and the request token cached earlier in this request are
        // stale. IAntiforgery keeps that state in an internal per-request
        // feature (IAntiforgeryFeature) and would otherwise reuse the
        // pre-rotation token without writing a cookie, so reset it and hide the
        // request's antiforgery cookie before minting a fresh pair bound to the
        // rotated session.
        context.Items[SessionAuthenticationDefaults.SessionTokenHashItemKey] =
            Convert.ToHexString(SessionTokenHasher.Hash(success.RotatedToken));

        ResetAntiforgeryRequestState(context);
        HideRequestAntiforgeryCookie(context, antiforgeryOptions.Value.Cookie.Name);

        var tokenSet = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers[CsrfMiddleware.HeaderName] = tokenSet.RequestToken;

        return Results.NoContent();
    }

    /// <summary>
    /// Removes the framework's cached antiforgery state for this request so the
    /// next mint re-evaluates the (rotated) session instead of reusing the
    /// token minted before rotation. The feature type lives in the framework's
    /// internals, so it is located by name.
    /// </summary>
    private static void ResetAntiforgeryRequestState(HttpContext context)
    {
        const string antiforgeryFeatureTypeName = "Microsoft.AspNetCore.Antiforgery.IAntiforgeryFeature";

        foreach (var feature in context.Features)
        {
            if (string.Equals(feature.Key.FullName, antiforgeryFeatureTypeName, StringComparison.Ordinal))
            {
                context.Features[feature.Key] = null;
                return;
            }
        }
    }

    /// <summary>
    /// Hides the request's antiforgery cookie so the next mint generates and
    /// stores a new cookie token instead of reusing the pre-rotation one.
    /// </summary>
    private static void HideRequestAntiforgeryCookie(HttpContext context, string? cookieName)
    {
        if (string.IsNullOrEmpty(cookieName))
        {
            return;
        }

        // Replace only the parsed view: the raw Cookie header (and every other
        // cookie value, including percent-encoded ones) is left untouched.
        var filtered = new FilteredRequestCookieCollection(context.Request.Cookies, cookieName);
        context.Features.Set<IRequestCookiesFeature>(new RequestCookiesFeature(filtered));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        AuthService authService,
        CancellationToken cancellationToken)
    {        // RequireAuthorization() already rejected requests without a valid
        // session; the claim is issued by the session authentication handler.
        var sessionIdValue = context.User.FindFirstValue(SessionAuthenticationDefaults.SessionIdClaim);
        if (!long.TryParse(sessionIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionId))
        {
            return AuthRequired();
        }

        await authService.LogoutAsync(sessionId, cancellationToken);
        SessionCookie.Delete(context.Response);

        return Results.NoContent();
    }

    private static async Task<IResult> GetSessionAsync(
        HttpContext context,
        AppDbContext dbContext,
        ICurrentActorProvider actorProvider,
        PermissionService permissionService,
        CancellationToken cancellationToken)
    {
        ResponseCache.NoStore(context);

        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            return AuthRequired();
        }

        // Single-user mode runs as the system actor without a user row; the
        // session payload reports all permissions directly (docs/API.md §5).
        if (actor.IsSystem)
        {
            return Results.Ok(AuthSessionResponse.SingleUser());
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == actor.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return AuthRequired();
        }

        return Results.Ok(await SessionPayloads.ForUserAsync(permissionService, user, cancellationToken));
    }

    private static IResult AuthRequired() =>
        ApiResults.Error(
            StatusCodes.Status401Unauthorized,
            ErrorCodes.AuthRequired,
            AuthRequiredMessage);

    private static IResult InvalidCredentials() =>
        ApiResults.Error(
            StatusCodes.Status401Unauthorized,
            ErrorCodes.InvalidCredentials,
            InvalidCredentialsMessage);

    /// <summary>
    /// Parses and validates the login body. Missing, blank, or non-string
    /// fields and malformed JSON produce <c>400 VALIDATION_FAILED</c> with
    /// <c>details.field_errors</c>.
    /// </summary>
    private static async Task<(LoginRequest? Request, IResult? Error)> ReadLoginRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return (null, BodyValidationError());
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, BodyValidationError());
            }

            var fieldErrors = new Dictionary<string, string[]>();
            var username = ReadRequiredString(root, "username", fieldErrors);
            var password = ReadRequiredString(root, "password", fieldErrors);
            if (fieldErrors.Count > 0)
            {
                return (null, ApiResults.ValidationError(fieldErrors));
            }

            return (new LoginRequest(username!, password!), null);
        }
    }

    private static string? ReadRequiredString(
        JsonElement root,
        string fieldName,
        Dictionary<string, string[]> fieldErrors)
    {
        if (!root.TryGetProperty(fieldName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            fieldErrors[fieldName] = [$"The {fieldName} field is required and must be a string."];
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            fieldErrors[fieldName] = [$"The {fieldName} field must not be blank."];
            return null;
        }

        return value;
    }

    /// <summary>
    /// Parses and validates the change-password body. Missing, blank, or
    /// non-string fields and malformed JSON produce <c>400 VALIDATION_FAILED</c>
    /// with <c>details.field_errors</c>; policy and same-password checks happen
    /// in <see cref="AuthService.ChangePasswordAsync"/>.
    /// </summary>
    private static async Task<(ChangePasswordRequest? Request, IResult? Error)> ReadChangePasswordRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return (null, BodyValidationError());
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, BodyValidationError());
            }

            var fieldErrors = new Dictionary<string, string[]>();
            var currentPassword = ReadRequiredString(root, "current_password", fieldErrors);
            var newPassword = ReadRequiredString(root, "new_password", fieldErrors);
            if (fieldErrors.Count > 0)
            {
                return (null, ApiResults.ValidationError(fieldErrors));
            }

            return (new ChangePasswordRequest(currentPassword!, newPassword!), null);
        }
    }

    private static IResult BodyValidationError() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });

    private static string? RemoteIpAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    private static string? UserAgent(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }

    private sealed record LoginRequest(string Username, string Password);

    private sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
}
