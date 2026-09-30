using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

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
            Permissions: EffectivePermissions.All,
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

        return app;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        AuthService authService,
        IAntiforgery antiforgery,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
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
                await LoginSuccessAsync(context, dbContext, antiforgery, success, cancellationToken),
            LoginResult.AccountLocked locked => ApiResults.Error(
                StatusCodes.Status423Locked,
                ErrorCodes.AccountLocked,
                AccountLockedMessage,
                new Dictionary<string, object?> { ["locked_until"] = locked.LockedUntil }),
            _ => InvalidCredentials(),
        };
    }

    private static async Task<IResult> LoginSuccessAsync(
        HttpContext context,
        AppDbContext dbContext,
        IAntiforgery antiforgery,
        LoginResult.Success success,
        CancellationToken cancellationToken)
    {
        // Persistent cookie for the 30-day idle window (docs/API.md §5).
        SessionCookie.Append(context.Response, success.RawToken, success.Session.IdleExpiresAt);

        // Mint the antiforgery pair for the new session so the client can send
        // X-CSRF-TOKEN immediately: the CSRF middleware ran while this request
        // was still unauthenticated and therefore skipped its authenticated
        // branch. Tokens embed both the session token hash and the claims-based
        // user id, so the token must be generated under the new session
        // principal to validate on subsequent authenticated requests.
        context.User = SessionClaims.Create(success.User, success.Session.Id);
        context.Items[SessionAuthenticationDefaults.SessionTokenHashItemKey] =
            Convert.ToHexString(success.Session.TokenHash);
        context.Response.Headers[CsrfMiddleware.HeaderName] =
            antiforgery.GetAndStoreTokens(context).RequestToken;

        return Results.Ok(await CreateMultiuserSessionAsync(dbContext, success.User, cancellationToken));
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        AuthService authService,
        CancellationToken cancellationToken)
    {
        // RequireAuthorization() already rejected requests without a valid
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
        AppDbContext dbContext,
        ICurrentActorProvider actorProvider,
        CancellationToken cancellationToken)
    {
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

        return Results.Ok(await CreateMultiuserSessionAsync(dbContext, user, cancellationToken));
    }

    private static async Task<AuthSessionResponse> CreateMultiuserSessionAsync(
        AppDbContext dbContext,
        User user,
        CancellationToken cancellationToken)
    {
        var grantedKeys = await dbContext.UserPermissions
            .AsNoTracking()
            .Where(permission => permission.UserId == user.Id)
            .Select(permission => permission.PermissionKey)
            .ToListAsync(cancellationToken);

        return new AuthSessionResponse(
            Authenticated: true,
            AuthMode: AuthSessionResponse.MultiuserAuthMode,
            User: new AuthUserResponse(
                user.Id,
                user.Username,
                EnumTextConverters.UserRoleToText(user.Role)),
            Permissions: EffectivePermissions.For(user.Role, grantedKeys),
            MustChangePassword: user.MustChangePassword);
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
}
