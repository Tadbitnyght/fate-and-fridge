using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Ingredient alias as returned by the API (display form, docs/API.md §22).</summary>
public sealed record IngredientAliasResponse(long Id, string Alias)
{
    public static IngredientAliasResponse From(IngredientAliasRecord record) =>
        new(record.Id, record.Alias);
}

/// <summary>Ingredient as returned by the API (display form, docs/API.md §22).</summary>
public sealed record IngredientResponse(
    long Id,
    string CanonicalName,
    long? DepartmentId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<IngredientAliasResponse> Aliases)
{
    public static IngredientResponse From(IngredientRecord record) =>
        new(
            record.Id,
            record.CanonicalName,
            record.DepartmentId,
            record.IsActive,
            record.CreatedAt,
            record.UpdatedAt,
            record.Aliases.Select(IngredientAliasResponse.From).ToArray());
}

/// <summary>Result of an ingredient merge (docs/API.md §22).</summary>
public sealed record IngredientMergeResponse(
    long SourceId,
    long TargetId,
    IReadOnlyList<string> SkippedAliases,
    int ReassignedReferenceCount)
{
    public static IngredientMergeResponse From(IngredientMergeResult.Success success) =>
        new(
            success.Source.Id,
            success.Target.Id,
            success.SkippedAliases,
            success.ReassignedReferenceCount);
}

/// <summary>
/// M2-08 ingredients routes (docs/API.md §22): authenticated reads, writes gated
/// by <c>reference_data.edit</c>, Admin-only hard delete, per-ingredient aliases,
/// and the shared canonical-name/alias uniqueness namespace.
/// </summary>
public static class IngredientEndpoints
{
    private const int DefaultLimit = 20;

    private const int MaxLimit = 100;

    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapIngredientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/ingredients").WithTags("Ingredients");

        group.MapGet("", ListIngredientsAsync).RequireAuthenticated();
        group.MapPost("", CreateIngredientAsync).RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPost("/merge", MergeIngredientsAsync).RequirePermission(PermissionKeys.ReferenceDataEdit);

        group.MapPatch("/{id:long}", UpdateIngredientAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPost("/{id:long}/aliases", AddAliasAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapDelete("/{id:long}/aliases/{aliasId:long}", RemoveAliasAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPost("/{id:long}/retire", RetireIngredientAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapDelete("/{id:long}", DeleteIngredientAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> ListIngredientsAsync(
        HttpContext context,
        IngredientService ingredients,
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

        var items = await ingredients.ListAsync(normalizedSearch, limit, includeRetired, cancellationToken);
        return Results.Ok(items.Select(IngredientResponse.From).ToArray());
    }

    private static async Task<IResult> CreateIngredientAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        IngredientService ingredients,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        string? canonicalName = null;
        if (!root.TryGetProperty("canonical_name", out var nameElement))
        {
            errors["canonical_name"] = ["canonical_name is required and must be a string."];
        }
        else if (nameElement.ValueKind != JsonValueKind.String)
        {
            errors["canonical_name"] = ["canonical_name must be a string."];
        }
        else
        {
            canonicalName = nameElement.GetString();
        }

        long? departmentId = null;
        if (root.TryGetProperty("department_id", out var departmentElement)
            && departmentElement.ValueKind != JsonValueKind.Null)
        {
            if (departmentElement.ValueKind != JsonValueKind.Number
                || !departmentElement.TryGetInt64(out var parsedDepartmentId))
            {
                errors["department_id"] = ["department_id must be an active department id."];
            }
            else
            {
                departmentId = parsedDepartmentId;
            }
        }

        IReadOnlyList<string>? aliases = null;
        if (root.TryGetProperty("aliases", out var aliasesElement))
        {
            if (aliasesElement.ValueKind != JsonValueKind.Array)
            {
                errors["aliases"] = ["aliases must be an array of strings."];
            }
            else
            {
                var values = new List<string>();
                foreach (var element in aliasesElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                    {
                        errors["aliases"] = ["aliases must be an array of strings."];
                        break;
                    }

                    values.Add(element.GetString()!);
                }

                if (!errors.ContainsKey("aliases"))
                {
                    aliases = values;
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

        var result = await ingredients.CreateAsync(
            canonicalName,
            departmentId,
            aliases,
            ipAddress,
            actorUserId,
            cancellationToken);
        return result switch
        {
            IngredientMutationResult.Success success => Results.Json(
                IngredientResponse.From(success.Ingredient),
                statusCode: StatusCodes.Status201Created),
            IngredientMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            IngredientMutationResult.Conflict conflict => IngredientConflict(conflict.ExistingId),
            IngredientMutationResult.AliasConflict conflict => AliasConflict(conflict.Details),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> MergeIngredientsAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        IngredientService ingredients,
        CancellationToken cancellationToken)
    {
        var (root, bodyError) = await ReadObjectAsync(context, cancellationToken);
        if (bodyError is not null)
        {
            return bodyError;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var sourceId = ReadRequiredId(root, "source_id", errors);
        var targetId = ReadRequiredId(root, "target_id", errors);
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

        var result = await ingredients.MergeAsync(
            sourceId!.Value,
            targetId!.Value,
            ipAddress,
            actorUserId,
            cancellationToken);
        return result switch
        {
            IngredientMergeResult.Success success => Results.Ok(IngredientMergeResponse.From(success)),
            IngredientMergeResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            _ => NotFound(),
        };
    }

    private static long? ReadRequiredId(
        JsonElement root,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(field, out var element))
        {
            errors[field] = [$"{field} is required."];
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
        {
            errors[field] = [$"{field} must be an integer."];
            return null;
        }

        return value;
    }

    private static async Task<IResult> UpdateIngredientAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        IngredientService ingredients,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var canonicalRequested = root.TryGetProperty("canonical_name", out var nameElement);
        string? canonicalName = null;
        if (canonicalRequested)
        {
            if (nameElement.ValueKind == JsonValueKind.String)
            {
                canonicalName = nameElement.GetString();
            }
            else
            {
                errors["canonical_name"] = ["canonical_name must be a string."];
            }
        }

        var departmentRequested = root.TryGetProperty("department_id", out var departmentElement);
        long? departmentId = null;
        if (departmentRequested && departmentElement.ValueKind != JsonValueKind.Null)
        {
            if (departmentElement.ValueKind == JsonValueKind.Number
                && departmentElement.TryGetInt64(out var parsedDepartmentId))
            {
                departmentId = parsedDepartmentId;
            }
            else
            {
                errors["department_id"] = ["department_id must be an active department id."];
            }
        }

        if (!canonicalRequested && !departmentRequested)
        {
            errors["body"] = ["At least one of canonical_name or department_id must be provided."];
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

        var result = await ingredients.UpdateAsync(
            id,
            canonicalName,
            canonicalRequested,
            departmentId,
            departmentRequested,
            ipAddress,
            actorUserId,
            cancellationToken);
        return result switch
        {
            IngredientMutationResult.Success success => Results.Ok(IngredientResponse.From(success.Ingredient)),
            IngredientMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            IngredientMutationResult.Conflict conflict => IngredientConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> AddAliasAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        IngredientService ingredients,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? alias = null;
        if (!root.TryGetProperty("alias", out var aliasElement))
        {
            errors["alias"] = ["alias is required and must be a string."];
        }
        else if (aliasElement.ValueKind != JsonValueKind.String)
        {
            errors["alias"] = ["alias must be a string."];
        }
        else
        {
            alias = aliasElement.GetString();
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

        var result = await ingredients.AddAliasAsync(
            id,
            alias,
            ipAddress,
            actorUserId,
            cancellationToken);
        return result switch
        {
            IngredientAliasMutationResult.Success success => Results.Json(
                IngredientAliasResponse.From(success.Alias),
                statusCode: StatusCodes.Status201Created),
            IngredientAliasMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            IngredientAliasMutationResult.AliasConflict conflict => AliasConflict(conflict.Details),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> RemoveAliasAsync(
        long id,
        long aliasId,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        IngredientService ingredients,
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

        var result = await ingredients.RemoveAliasAsync(
            id,
            aliasId,
            ipAddress,
            actorUserId,
            cancellationToken);
        return result switch
        {
            IngredientAliasMutationResult.Success => Results.NoContent(),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> RetireIngredientAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        IngredientService ingredients,
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

        var result = await ingredients.RetireAsync(id, ipAddress, actorUserId, cancellationToken);
        return result switch
        {
            IngredientMutationResult.Success success => Results.Ok(IngredientResponse.From(success.Ingredient)),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> DeleteIngredientAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        IngredientService ingredients,
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

        var result = await ingredients.DeleteAsync(id, ipAddress, actorUserId, cancellationToken);
        return result switch
        {
            IngredientMutationResult.Success => Results.NoContent(),
            IngredientMutationResult.InUse inUse => IngredientInUse(inUse.UsageCount, inUse.Reason),
            _ => NotFound(),
        };
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

    private static IResult IngredientConflict(long? existingId)
    {
        var details = new Dictionary<string, object?>();
        if (existingId is not null)
        {
            details["existing_id"] = existingId.Value;
        }

        return ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.IngredientExists,
            "An ingredient with this name or alias already exists.",
            details);
    }

    private static IResult AliasConflict(IReadOnlyDictionary<string, object?> details) =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.AliasExists,
            "The alias is already used in the ingredient namespace.",
            details);

    private static IResult IngredientInUse(int usageCount, string? reason)
    {
        var details = new Dictionary<string, object?>
        {
            ["usage_count"] = usageCount,
        };
        if (reason is not null)
        {
            details["reason"] = reason;
        }

        return ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.IngredientInUse,
            reason is null
                ? $"The ingredient is still referenced by {usageCount} recipe ingredient(s); merge or retire it instead."
                : "The ingredient is still the target of a merge; keep it retired for traceability.",
            details);
    }

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
            "The ingredient does not exist.");
}
