using System.Text.Json;
using System.Text.Json.Serialization;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// User payload (docs/API.md §6/§21). <see cref="TemporaryPassword"/> is present
/// exactly once when an account was created without an explicit password; it is
/// never logged or persisted in plaintext.
/// </summary>
public sealed record UserResponse(
    long Id,
    string Username,
    string Role,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset? LastLoginAt,
    IReadOnlyList<string> Permissions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TemporaryPassword)
{
    public static UserResponse From(UserRecord record, string? temporaryPassword = null) =>
        new(
            record.Id,
            record.Username,
            record.Role,
            record.IsActive,
            record.MustChangePassword,
            record.LastLoginAt,
            record.Permissions,
            temporaryPassword);
}

/// <summary>Reset response: the temporary password is returned exactly once (docs/API.md §5).</summary>
public sealed record ResetPasswordResponse(string TemporaryPassword);

/// <summary>
/// User management routes (docs/API.md §5/§6/§21). Admin-only via the M1-09
/// <c>RequireAdmin()</c> filter (the single-user system actor also passes);
/// members receive <c>403 FORBIDDEN</c> and unauthenticated callers
/// <c>401 AUTH_REQUIRED</c>.
/// </summary>
public static class UserEndpoints
{
    private const string NotFoundMessage = "The requested user does not exist.";

    private const string LastAdminMessage =
        "The last active Admin cannot be disabled or demoted.";

    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/users").WithTags("Users");

        group.MapGet("", ListUsersAsync).RequireAdmin();
        group.MapPost("", CreateUserAsync).RequireAdmin();
        group.MapGet("/{id:long}", GetUserAsync).RequireAdmin();
        group.MapPatch("/{id:long}", UpdateUserAsync).RequireAdmin();
        group.MapPatch("/{id:long}/permissions", ReplacePermissionsAsync).RequireAdmin();
        group.MapPost("/{id:long}/disable", DisableUserAsync).RequireAdmin();
        group.MapPost("/{id:long}/enable", EnableUserAsync).RequireAdmin();
        group.MapPost("/{id:long}/reset-password", ResetPasswordAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> ListUsersAsync(
        UserManagementService users,
        CancellationToken cancellationToken)
    {
        var records = await users.ListAsync(cancellationToken);
        return Results.Ok(records.Select(record => UserResponse.From(record)).ToArray());
    }

    private static async Task<IResult> GetUserAsync(
        long id,
        UserManagementService users,
        CancellationToken cancellationToken)
    {
        var record = await users.GetAsync(id, cancellationToken);
        return record is null ? NotFound() : Results.Ok(UserResponse.From(record));
    }

    private static async Task<IResult> CreateUserAsync(
        HttpContext context,
        UserManagementService users,
        IOptions<JsonOptions> jsonOptions,
        CancellationToken cancellationToken)
    {
        // The response may carry a one-time temporary password: never cache it.
        ResponseCache.NoStore(context);

        var (request, error) = await ReadCreateRequestAsync(context, cancellationToken);
        if (request is null)
        {
            return error!;
        }

        var result = await users.CreateAsync(
            request.Username,
            request.Role,
            request.Password,
            request.PasswordProvided,
            cancellationToken);

        return result switch
        {
            UserMutationResult.Success success => Results.Json(
                UserResponse.From(success.User, success.TemporaryPassword),
                jsonOptions.Value.SerializerOptions,
                statusCode: StatusCodes.Status201Created),
            UserMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            UserMutationResult.LastAdminProtected => LastAdminProtected(),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> UpdateUserAsync(
        long id,
        HttpContext context,
        UserManagementService users,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadObjectAsync(context, cancellationToken);
        if (parsed is null)
        {
            return BodyError();
        }

        using var document = parsed.Value.Document;
        var root = parsed.Value.Root;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var renameRequested = root.TryGetProperty("username", out var usernameElement);
        string? username = null;
        if (renameRequested)
        {
            if (usernameElement.ValueKind == JsonValueKind.String)
            {
                username = usernameElement.GetString();
            }
            else
            {
                errors["username"] = ["username must be a string."];
            }
        }

        var roleRequested = root.TryGetProperty("role", out var roleElement);
        string? roleText = null;
        if (roleRequested)
        {
            if (roleElement.ValueKind == JsonValueKind.String)
            {
                roleText = roleElement.GetString();
            }
            else
            {
                errors["role"] = ["role must be a string."];
            }
        }

        if (!renameRequested && !roleRequested)
        {
            errors["body"] = ["At least one of username or role must be provided."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var result = await users.UpdateAsync(
            id,
            username,
            renameRequested,
            roleText,
            roleRequested,
            cancellationToken);

        return result switch
        {
            UserMutationResult.Success success => Results.Ok(UserResponse.From(success.User)),
            UserMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            UserMutationResult.LastAdminProtected => LastAdminProtected(),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> ReplacePermissionsAsync(
        long id,
        HttpContext context,
        UserManagementService users,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadObjectAsync(context, cancellationToken);
        if (parsed is null)
        {
            return BodyError();
        }

        using var document = parsed.Value.Document;
        var root = parsed.Value.Root;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        IReadOnlyList<string> permissions = [];

        if (!root.TryGetProperty("permissions", out var permissionsElement)
            || permissionsElement.ValueKind != JsonValueKind.Array)
        {
            errors["permissions"] = ["permissions must be an array of permission keys."];
        }
        else
        {
            var parsedPermissions = new List<string>();
            foreach (var element in permissionsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                {
                    errors["permissions"] = ["permissions must be an array of permission keys."];
                    break;
                }

                parsedPermissions.Add(element.GetString()!);
            }

            if (!errors.ContainsKey("permissions"))
            {
                permissions = parsedPermissions;
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var result = await users.ReplacePermissionsAsync(id, permissions, cancellationToken);

        return result switch
        {
            UserMutationResult.Success success => Results.Ok(UserResponse.From(success.User)),
            UserMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            _ => NotFound(),
        };
    }

    private static Task<IResult> DisableUserAsync(
        long id,
        UserManagementService users,
        CancellationToken cancellationToken) =>
        SetActiveAsync(id, activate: false, users, cancellationToken);

    private static Task<IResult> EnableUserAsync(
        long id,
        UserManagementService users,
        CancellationToken cancellationToken) =>
        SetActiveAsync(id, activate: true, users, cancellationToken);

    private static async Task<IResult> SetActiveAsync(
        long id,
        bool activate,
        UserManagementService users,
        CancellationToken cancellationToken)
    {
        var result = await users.SetActiveAsync(id, activate, cancellationToken);

        return result switch
        {
            UserMutationResult.Success success => Results.Ok(UserResponse.From(success.User)),
            UserMutationResult.LastAdminProtected => LastAdminProtected(),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> ResetPasswordAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        AuthService authService,
        CancellationToken cancellationToken)
    {
        ResponseCache.NoStore(context);

        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            // RequireAdmin() already rejected unauthenticated requests; keep the
            // defensive envelope so the handler cannot fail open.
            return ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage);
        }

        var result = await authService.ResetPasswordAsync(
            id,
            actor.IsSystem ? null : actor.UserId,
            context.Connection.RemoteIpAddress?.ToString(),
            UserAgent(context),
            cancellationToken);

        return result switch
        {
            ResetPasswordResult.Success success =>
                Results.Ok(new ResetPasswordResponse(success.TemporaryPassword)),
            _ => NotFound(),
        };
    }

    private static async Task<(CreateUserRequest? Request, IResult? Error)> ReadCreateRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var parsed = await ReadObjectAsync(context, cancellationToken);
        if (parsed is null)
        {
            return (null, BodyError());
        }

        using var document = parsed.Value.Document;
        var root = parsed.Value.Root;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        string? username = null;
        if (root.TryGetProperty("username", out var usernameElement))
        {
            if (usernameElement.ValueKind == JsonValueKind.String)
            {
                username = usernameElement.GetString();
            }
            else
            {
                errors["username"] = ["username must be a string."];
            }
        }

        string? role = null;
        if (root.TryGetProperty("role", out var roleElement))
        {
            if (roleElement.ValueKind == JsonValueKind.String)
            {
                role = roleElement.GetString();
            }
            else
            {
                errors["role"] = ["role must be a string."];
            }
        }

        string? password = null;
        var passwordProvided = false;
        if (root.TryGetProperty("password", out var passwordElement)
            && passwordElement.ValueKind != JsonValueKind.Null)
        {
            passwordProvided = true;
            if (passwordElement.ValueKind == JsonValueKind.String)
            {
                password = passwordElement.GetString();
            }
            else
            {
                errors["password"] = ["password must be a string."];
            }
        }

        if (errors.Count > 0)
        {
            return (null, ApiResults.ValidationError(errors));
        }

        return (new CreateUserRequest(username, role, password, passwordProvided), null);
    }

    private static async Task<(JsonDocument Document, JsonElement Root)?> ReadObjectAsync(
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
            return null;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return null;
        }

        return (document, document.RootElement);
    }

    private static IResult BodyError() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });

    private static IResult NotFound() =>
        ApiResults.Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, NotFoundMessage);

    private static IResult LastAdminProtected() =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.LastAdminProtected,
            LastAdminMessage);

    private static string? UserAgent(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent;
    }

    private sealed record CreateUserRequest(
        string? Username,
        string? Role,
        string? Password,
        bool PasswordProvided);
}
