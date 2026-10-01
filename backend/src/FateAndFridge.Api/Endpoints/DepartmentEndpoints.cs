using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Department as returned by the API (display form, docs/API.md §22).</summary>
public sealed record DepartmentResponse(
    long Id,
    string Name,
    int SortOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static DepartmentResponse From(DepartmentRecord record) =>
        new(
            record.Id,
            record.Name,
            record.SortOrder,
            record.IsActive,
            record.CreatedAt,
            record.UpdatedAt);
}

/// <summary>
/// M2-05 departments routes (docs/API.md §22): authenticated reads, writes
/// gated by <c>reference_data.edit</c>, Admin-only hard delete, and the
/// complete-permutation order update.
/// </summary>
public static class DepartmentEndpoints
{
    private const int DefaultLimit = 20;

    private const int MaxLimit = 100;

    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapDepartmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/departments").WithTags("Departments");

        group.MapGet("", ListDepartmentsAsync).RequireAuthenticated();
        group.MapPost("", CreateDepartmentAsync).RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPatch("/{id:long}", UpdateDepartmentAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPost("/{id:long}/retire", RetireDepartmentAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapDelete("/{id:long}", DeleteDepartmentAsync).RequireAdmin();
        group.MapPut("/order", ReorderDepartmentsAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);

        return app;
    }

    private static async Task<IResult> ListDepartmentsAsync(
        HttpContext context,
        DepartmentService departments,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var limit = DefaultLimit;
        if (context.Request.Query.TryGetValue("limit", out var limitValues))
        {
            var rawLimit = limitValues.Count > 0 ? limitValues[^1] : null;
            if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1)
            {
                errors["limit"] = ["limit must be a positive integer."];
            }
            else
            {
                limit = Math.Min(parsed, MaxLimit);
            }
        }

        var includeRetired = false;
        if (context.Request.Query.TryGetValue("include_retired", out var retiredValues))
        {
            var rawRetired = retiredValues.Count > 0 ? retiredValues[^1] : null;
            if (!bool.TryParse(rawRetired, out includeRetired))
            {
                errors["include_retired"] = ["include_retired must be true or false."];
            }
        }

        string? normalizedSearch = null;
        if (context.Request.Query.TryGetValue("search", out var searchValues))
        {
            var rawSearch = searchValues.Count > 0 ? searchValues[^1] : null;
            if (!string.IsNullOrWhiteSpace(rawSearch))
            {
                if (ReferenceDataNormalizer.Normalize(rawSearch)
                    is ReferenceDataNormalizationResult.Success search)
                {
                    normalizedSearch = search.Normalized;
                }
                else
                {
                    errors["search"] = ["search contains characters that cannot be matched."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var items = await departments.ListAsync(
            normalizedSearch,
            limit,
            includeRetired,
            cancellationToken);
        return Results.Ok(items.Select(DepartmentResponse.From).ToArray());
    }

    private static async Task<IResult> CreateDepartmentAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        DepartmentService departments,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? name = null;
        if (!root.TryGetProperty("name", out var nameElement))
        {
            errors["name"] = ["name is required and must be a string."];
        }
        else if (nameElement.ValueKind != JsonValueKind.String)
        {
            errors["name"] = ["name must be a string."];
        }
        else
        {
            name = nameElement.GetString();
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await departments.CreateAsync(name, ipAddress, actorUserId, cancellationToken);
        return result switch
        {
            DepartmentMutationResult.Success success => Results.Json(
                DepartmentResponse.From(success.Department),
                statusCode: StatusCodes.Status201Created),
            DepartmentMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> UpdateDepartmentAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        DepartmentService departments,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var renameRequested = root.TryGetProperty("name", out var nameElement);
        string? name = null;
        if (renameRequested)
        {
            if (nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString();
            }
            else
            {
                errors["name"] = ["name must be a string."];
            }
        }

        var activeRequested = root.TryGetProperty("is_active", out var activeElement);
        bool? isActive = null;
        if (activeRequested)
        {
            if (activeElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isActive = activeElement.GetBoolean();
            }
            else
            {
                errors["is_active"] = ["is_active must be true or false."];
            }
        }

        if (!renameRequested && !activeRequested)
        {
            errors["body"] = ["At least one of name or is_active must be provided."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await departments.UpdateAsync(
            id,
            name,
            renameRequested,
            isActive,
            activeRequested,
            ipAddress,
            actorUserId,
            cancellationToken);
        return result switch
        {
            DepartmentMutationResult.Success success => Results.Ok(
                DepartmentResponse.From(success.Department)),
            DepartmentMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> RetireDepartmentAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        DepartmentService departments,
        CancellationToken cancellationToken)
    {
        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await departments.RetireAsync(id, ipAddress, actorUserId, cancellationToken);
        return result switch
        {
            DepartmentMutationResult.Success success => Results.Ok(
                DepartmentResponse.From(success.Department)),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> DeleteDepartmentAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        DepartmentService departments,
        CancellationToken cancellationToken)
    {
        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await departments.DeleteAsync(id, ipAddress, actorUserId, cancellationToken);
        return result switch
        {
            DepartmentMutationResult.Success => Results.NoContent(),
            DepartmentMutationResult.InUse inUse => DepartmentInUse(inUse.IngredientCount),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> ReorderDepartmentsAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        DepartmentService departments,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        List<long>? ids = null;
        if (!root.TryGetProperty("ids", out var idsElement))
        {
            errors["ids"] = ["ids is required and must be an array of department ids."];
        }
        else if (idsElement.ValueKind != JsonValueKind.Array)
        {
            errors["ids"] = ["ids must be an array of department ids."];
        }
        else
        {
            ids = [];
            foreach (var element in idsElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
                {
                    ids.Add(value);
                }
                else
                {
                    errors["ids"] = ["ids must contain only integer department ids."];
                    break;
                }
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await departments.ReorderAsync(ids, ipAddress, actorUserId, cancellationToken);
        if (result is DepartmentReorderResult.ValidationFailed failed)
        {
            return ApiResults.ValidationError(failed.FieldErrors);
        }

        var success = (DepartmentReorderResult.Success)result;
        return Results.Ok(success.Departments.Select(DepartmentResponse.From).ToArray());
    }

    private static async Task<(long? ActorUserId, string? IpAddress, IResult? Error)> ResolveActorAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        CancellationToken cancellationToken)
    {
        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            // The permission filter already rejected unauthenticated requests;
            // keep the defensive envelope so the handler cannot fail open.
            return (null, null, ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage));
        }

        return (
            actor.IsSystem ? null : actor.UserId,
            context.Connection.RemoteIpAddress?.ToString(),
            null);
    }

    private static IResult DepartmentInUse(int ingredientCount) =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.DepartmentInUse,
            $"The department is still referenced by {ingredientCount} ingredient(s); retire it instead.",
            new Dictionary<string, object?>
            {
                ["ingredient_count"] = ingredientCount,
            });

    private static async Task<(JsonElement Root, IResult? Error)> ReadObjectAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return (default, body.Error);
        }

        using var document = body.Document!;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (default, BodyError());
        }

        return (document.RootElement.Clone(), null);
    }

    private static IResult BodyError() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });

    private static IResult NotFound() =>
        ApiResults.Error(
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            "The department does not exist.");
}
