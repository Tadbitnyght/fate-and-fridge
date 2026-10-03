using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Recipe list item as returned by <c>GET /api/v1/recipes</c> (M3-02). The
/// image, last-eaten, and times-eaten placeholders stay null until M3-04 and
/// the history milestones land.
/// </summary>
public sealed record RecipeListItemResponse(
    long Id,
    string Name,
    string Status,
    bool IsFavorite,
    bool IsVegan,
    bool IsVegetarian,
    object? Image,
    object? LastEaten,
    int? TimesEaten,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static RecipeListItemResponse From(RecipeRecord record) =>
        new(
            record.Id,
            record.Name,
            EnumTextConverters.RecipeStatusToText(record.Status),
            record.IsFavorite,
            record.IsVegan,
            record.IsVegetarian,
            null,
            null,
            null,
            record.CreatedAt,
            record.UpdatedAt);
}

/// <summary>
/// Complete recipe detail document (M3-02). Every field is always present;
/// the nested collections stay empty until M3-05 … M3-08 fill them.
/// </summary>
public sealed record RecipeDetailResponse(
    long Id,
    string Name,
    string? Description,
    string? SourceSite,
    string? SourceUrl,
    string? Author,
    int? PrepMinutes,
    int? CookMinutes,
    string? Notes,
    string Status,
    bool IsFavorite,
    bool IsVegan,
    bool IsVegetarian,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    object? Image,
    IReadOnlyList<object> IngredientGroups,
    IReadOnlyList<object> Ingredients,
    IReadOnlyList<object> InstructionSections,
    IReadOnlyList<object> Proteins,
    IReadOnlyList<object> Tags)
{
    public static RecipeDetailResponse From(RecipeRecord record) =>
        new(
            record.Id,
            record.Name,
            record.Description,
            record.SourceSite,
            record.SourceUrl,
            record.Author,
            record.PrepMinutes,
            record.CookMinutes,
            record.Notes,
            EnumTextConverters.RecipeStatusToText(record.Status),
            record.IsFavorite,
            record.IsVegan,
            record.IsVegetarian,
            record.CreatedAt,
            record.UpdatedAt,
            null,
            Array.Empty<object>(),
            Array.Empty<object>(),
            Array.Empty<object>(),
            Array.Empty<object>(),
            Array.Empty<object>());
}

/// <summary>Paged recipe list envelope: <c>{ items, total, limit, offset }</c> (M3-02).</summary>
public sealed record RecipeListResponse(
    IReadOnlyList<RecipeListItemResponse> Items,
    int Total,
    int Limit,
    int Offset);

/// <summary>
/// M3-02 recipe base metadata and status lifecycle routes (docs/M3_PLAN.md §2):
/// reads gated by <c>recipes.view</c>, writes by <c>recipes.edit</c>, with the
/// M1-09 enforcement helpers and the shared error envelope.
/// </summary>
public static class RecipeEndpoints
{
    private const int DefaultLimit = 24;

    private const int MaxLimit = 100;

    /// <summary>
    /// Request cap for recipe payloads: comfortable room for two 20,000-character
    /// text fields at their worst-case UTF-8 expansion plus metadata, while still
    /// bounding oversized bodies before they are buffered.
    /// </summary>
    private const int MaxBodyBytes = 256 * 1024;

    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapRecipeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/recipes").WithTags("Recipes");

        group.MapGet("", ListRecipesAsync).RequirePermission(PermissionKeys.RecipesView);
        group.MapPost("", CreateRecipeAsync).RequirePermission(PermissionKeys.RecipesEdit);
        group.MapGet("/{id:long}", GetRecipeAsync).RequirePermission(PermissionKeys.RecipesView);
        group.MapPatch("/{id:long}", UpdateRecipeAsync).RequirePermission(PermissionKeys.RecipesEdit);

        return app;
    }

    private static async Task<IResult> ListRecipesAsync(
        HttpContext context,
        RecipeService recipes,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        // The default sort is the fixed recently_added order until M3-09 adds
        // the named sorts, so no sort parameter is accepted yet.
        var limit = DefaultLimit;
        if (context.Request.Query.TryGetValue("limit", out var limitValues))
        {
            var rawLimit = limitValues.Count > 0 ? limitValues[^1] : null;
            if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit)
                || parsedLimit < 1)
            {
                errors["limit"] = [$"limit must be an integer between 1 and {MaxLimit}."];
            }
            else
            {
                limit = Math.Min(parsedLimit, MaxLimit);
            }
        }

        var offset = 0;
        if (context.Request.Query.TryGetValue("offset", out var offsetValues))
        {
            var rawOffset = offsetValues.Count > 0 ? offsetValues[^1] : null;
            if (!int.TryParse(rawOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset)
                || parsedOffset < 0)
            {
                errors["offset"] = ["offset must be a nonnegative integer."];
            }
            else
            {
                offset = parsedOffset;
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var favorite = ReadOptionalBool(context, "favorite", errors);
        var vegan = ReadOptionalBool(context, "vegan", errors);
        var vegetarian = ReadOptionalBool(context, "vegetarian", errors);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (items, total) = await recipes.ListAsync(
            limit,
            offset,
            favorite,
            vegan,
            vegetarian,
            cancellationToken);
        return Results.Ok(new RecipeListResponse(
            items.Select(RecipeListItemResponse.From).ToArray(),
            total,
            limit,
            offset));
    }

    private static bool? ReadOptionalBool(
        HttpContext context,
        string name,
        Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue(name, out var values))
        {
            return null;
        }

        var raw = values.Count > 0 ? values[^1] : null;
        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        errors[name] = [$"{name} must be true or false."];
        return null;
    }

    private static async Task<IResult> CreateRecipeAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RecipeService recipes,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var request = ReadWriteRequest(root, requireName: true, errors);
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

        var result = await recipes.CreateAsync(request, ipAddress, actorUserId, cancellationToken);
        return result switch
        {
            RecipeMutationResult.Success success => Results.Json(
                RecipeDetailResponse.From(success.Recipe),
                statusCode: StatusCodes.Status201Created),
            RecipeMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            RecipeMutationResult.Conflict conflict => RecipeConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> GetRecipeAsync(
        long id,
        RecipeService recipes,
        CancellationToken cancellationToken)
    {
        var recipe = await recipes.GetAsync(id, cancellationToken);
        return recipe is null ? NotFound() : Results.Ok(RecipeDetailResponse.From(recipe));
    }

    private static async Task<IResult> UpdateRecipeAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RecipeService recipes,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var request = ReadWriteRequest(root, requireName: false, errors);
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

        var result = await recipes.UpdateAsync(id, request, ipAddress, actorUserId, cancellationToken);
        return result switch
        {
            RecipeMutationResult.Success success => Results.Ok(RecipeDetailResponse.From(success.Recipe)),
            RecipeMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            RecipeMutationResult.Conflict conflict => RecipeConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static RecipeWriteRequest ReadWriteRequest(
        JsonElement root,
        bool requireName,
        Dictionary<string, string[]> errors)
    {
        var name = default(RecipeField<string?>);
        if (!root.TryGetProperty("name", out var nameElement))
        {
            if (requireName)
            {
                errors["name"] = ["name is required and must be a string."];
            }
        }
        else if (nameElement.ValueKind != JsonValueKind.String)
        {
            errors["name"] = ["name must be a string."];
        }
        else
        {
            name = RecipeField<string?>.Set(nameElement.GetString());
        }

        var request = new RecipeWriteRequest(
            name,
            ReadOptionalString(root, "description", errors),
            ReadOptionalString(root, "source_site", errors),
            ReadOptionalString(root, "source_url", errors),
            ReadOptionalString(root, "author", errors),
            ReadOptionalInteger(root, "prep_minutes", errors),
            ReadOptionalInteger(root, "cook_minutes", errors),
            ReadOptionalString(root, "notes", errors),
            ReadOptionalStatus(root, errors),
            ReadOptionalBoolean(root, "is_favorite", errors),
            ReadOptionalBoolean(root, "is_vegan", errors),
            ReadOptionalBoolean(root, "is_vegetarian", errors));

        // Mirrors the M2 PATCH behavior: at least one supported field must be
        // present, while unknown properties are ignored.
        if (!requireName && errors.Count == 0 && IsEmpty(request))
        {
            errors["body"] = ["At least one supported field must be provided."];
        }

        return request;
    }

    private static bool IsEmpty(RecipeWriteRequest request) =>
        !request.Name.IsSet
        && !request.Description.IsSet
        && !request.SourceSite.IsSet
        && !request.SourceUrl.IsSet
        && !request.Author.IsSet
        && !request.PrepMinutes.IsSet
        && !request.CookMinutes.IsSet
        && !request.Notes.IsSet
        && !request.Status.IsSet
        && !request.IsFavorite.IsSet
        && !request.IsVegan.IsSet
        && !request.IsVegetarian.IsSet;

    private static RecipeField<string?> ReadOptionalString(
        JsonElement root,
        string property,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return default;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return RecipeField<string?>.Set(element.GetString());
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return RecipeField<string?>.Set(null);
        }

        errors[property] = [$"{property} must be a string or null."];
        return default;
    }

    private static RecipeField<int?> ReadOptionalInteger(
        JsonElement root,
        string property,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return default;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return RecipeField<int?>.Set(null);
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value))
        {
            return RecipeField<int?>.Set(value);
        }

        errors[property] = [$"{property} must be an integer between 0 and 1440."];
        return default;
    }

    private static RecipeField<string?> ReadOptionalStatus(
        JsonElement root,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty("status", out var element))
        {
            return default;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return RecipeField<string?>.Set(element.GetString());
        }

        errors["status"] = ["status must be one of normal, manual_only, or retired."];
        return default;
    }

    private static RecipeField<bool?> ReadOptionalBoolean(
        JsonElement root,
        string property,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return default;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return RecipeField<bool?>.Set(element.GetBoolean());
        }

        errors[property] = [$"{property} must be true or false."];
        return default;
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

    private static IResult RecipeConflict(long? existingId)
    {
        var details = new Dictionary<string, object?>();
        if (existingId is not null)
        {
            details["existing_id"] = existingId.Value;
        }

        return ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.RecipeExists,
            "A recipe with this name already exists.",
            details);
    }

    private static async Task<(JsonElement Root, IResult? Error)> ReadObjectAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(
            context,
            MaxBodyBytes,
            cancellationToken: cancellationToken);
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
            "The recipe does not exist.");
}
