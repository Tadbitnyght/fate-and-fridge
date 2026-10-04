using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// Ingredient reference embedded in a recipe ingredient response.
/// </summary>
public sealed record RecipeIngredientReferenceResponse(long Id, string Name, bool IsActive);

/// <summary>Nullable unit reference embedded in a recipe ingredient response.</summary>
public sealed record RecipeUnitReferenceResponse(long Id, string Symbol, string DisplayName, bool IsActive);

/// <summary>
/// One ingredient line of the recipe document (M3-06): numeric decimal
/// quantity or null, optional unit, optional preparation note, and sort order.
/// </summary>
public sealed record RecipeIngredientResponse(
    long Id,
    RecipeIngredientReferenceResponse Ingredient,
    decimal? Quantity,
    RecipeUnitReferenceResponse? Unit,
    string? PreparationNote,
    bool IsOptional,
    int SortOrder)
{
    public static RecipeIngredientResponse From(RecipeIngredientRecord record) =>
        new(
            record.Id,
            new RecipeIngredientReferenceResponse(
                record.Ingredient.Id,
                record.Ingredient.Name,
                record.Ingredient.IsActive),
            record.Quantity,
            record.Unit is null
                ? null
                : new RecipeUnitReferenceResponse(
                    record.Unit.Id,
                    record.Unit.Symbol,
                    record.Unit.DisplayName,
                    record.Unit.IsActive),
            record.PreparationNote,
            record.IsOptional,
            record.SortOrder);
}

/// <summary>Ordered ingredient group with its nested lines (M3-05/M3-06).</summary>
public sealed record RecipeIngredientGroupResponse(
    long Id,
    string? Title,
    int SortOrder,
    IReadOnlyList<RecipeIngredientResponse> Ingredients)
{
    public static RecipeIngredientGroupResponse From(RecipeIngredientGroupRecord record) =>
        new(
            record.Id,
            record.Title,
            record.SortOrder,
            record.Ingredients.Select(RecipeIngredientResponse.From).ToArray());
}

/// <summary>One ordered instruction step (M3-07).</summary>
public sealed record RecipeInstructionStepResponse(long Id, string Text, int SortOrder)
{
    public static RecipeInstructionStepResponse From(RecipeInstructionStepRecord record) =>
        new(record.Id, record.Text, record.SortOrder);
}

/// <summary>Ordered instruction section with its nested steps (M3-07).</summary>
public sealed record RecipeInstructionSectionResponse(
    long Id,
    string? Title,
    int SortOrder,
    IReadOnlyList<RecipeInstructionStepResponse> Steps)
{
    public static RecipeInstructionSectionResponse From(RecipeInstructionSectionRecord record) =>
        new(
            record.Id,
            record.Title,
            record.SortOrder,
            record.Steps.Select(RecipeInstructionStepResponse.From).ToArray());
}

/// <summary>A protein association as returned on a recipe.</summary>
public sealed record RecipeProteinResponse(long Id, string Name, bool IsActive)
{
    public static RecipeProteinResponse From(RecipeProteinRecord record) =>
        new(record.Id, record.Name, record.IsActive);
}

/// <summary>A tag association as returned on a recipe.</summary>
public sealed record RecipeTagResponse(long Id, string Name)
{
    public static RecipeTagResponse From(RecipeTagRecord record) => new(record.Id, record.Name);
}

/// <summary>
/// History statistics placeholder on the recipe detail document (M3-09). All
/// values stay null until M5/M11 record history; the keys are always present so
/// clients never see fake zeros.
/// </summary>
public sealed record RecipeStatisticsResponse(
    DateTimeOffset? LastEaten,
    DateTimeOffset? NextPlanned,
    int? TimesEaten)
{
    public static RecipeStatisticsResponse Empty { get; } = new(null, null, null);
}

/// <summary>
/// Recipe list item as returned by <c>GET /api/v1/recipes</c> (M3-09). The
/// image is embedded since M3-04; last-eaten and times-eaten stay null until
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
            record.Image is null ? null : RecipeImageResponse.From(record.Id, record.Image),
            null,
            null,
            record.CreatedAt,
            record.UpdatedAt);
}

/// <summary>
/// Complete recipe detail document (M3-09): every base field is always present,
/// the active image is embedded (M3-04), and the nested ingredient, instruction,
/// protein, and tag slices are filled (M3-05 … M3-08).
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
    IReadOnlyList<RecipeIngredientGroupResponse> IngredientGroups,
    IReadOnlyList<RecipeIngredientResponse> Ingredients,
    IReadOnlyList<RecipeInstructionSectionResponse> InstructionSections,
    IReadOnlyList<RecipeProteinResponse> Proteins,
    IReadOnlyList<RecipeTagResponse> Tags,
    RecipeStatisticsResponse Statistics)
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
            record.Image is null ? null : RecipeImageResponse.From(record.Id, record.Image),
            record.IngredientGroups.Select(RecipeIngredientGroupResponse.From).ToArray(),
            record.Ingredients.Select(RecipeIngredientResponse.From).ToArray(),
            record.InstructionSections.Select(RecipeInstructionSectionResponse.From).ToArray(),
            record.Proteins.Select(RecipeProteinResponse.From).ToArray(),
            record.Tags.Select(RecipeTagResponse.From).ToArray(),
            RecipeStatisticsResponse.Empty);
}

/// <summary>Paged recipe list envelope: <c>{ items, total, limit, offset }</c> (M3-09).</summary>
public sealed record RecipeListResponse(
    IReadOnlyList<RecipeListItemResponse> Items,
    int Total,
    int Limit,
    int Offset);

/// <summary>
/// M3 recipe document routes (docs/M3_PLAN.md §2): reads gated by
/// <c>recipes.view</c>, writes by <c>recipes.edit</c>, with the M1-09
/// enforcement helpers and the shared error envelope.
/// </summary>
public static class RecipeEndpoints
{
    private const int DefaultLimit = 24;

    private const int MaxLimit = 100;

    /// <summary>
    /// Request cap for recipe payloads: comfortable room for nested collections
    /// of 20,000-character text fields at their worst-case UTF-8 expansion,
    /// while still bounding oversized bodies before they are buffered.
    /// </summary>
    private const int MaxBodyBytes = 1024 * 1024;

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

        var limit = ReadLimit(context, errors);
        var offset = ReadOffset(context, errors);
        var favorite = ReadOptionalBool(context, "favorite", errors);
        var vegan = ReadOptionalBool(context, "vegan", errors);
        var vegetarian = ReadOptionalBool(context, "vegetarian", errors);
        var status = ReadOptionalStatus(context, errors);
        var proteinId = ReadOptionalId(context, "protein_id", errors);
        var tagId = ReadOptionalId(context, "tag_id", errors);
        var sort = ReadSort(context, errors);
        var search = ReadSearch(context, errors);

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (items, total) = await recipes.ListAsync(
            limit,
            offset,
            search,
            status,
            favorite,
            vegan,
            vegetarian,
            proteinId,
            tagId,
            sort,
            cancellationToken);
        return Results.Ok(new RecipeListResponse(
            items.Select(RecipeListItemResponse.From).ToArray(),
            total,
            limit,
            offset));
    }

    private static int ReadLimit(HttpContext context, Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue("limit", out var values))
        {
            return DefaultLimit;
        }

        var raw = values.Count > 0 ? values[^1] : null;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            errors["limit"] = [$"limit must be an integer between 1 and {MaxLimit}."];
            return DefaultLimit;
        }

        return Math.Min(parsed, MaxLimit);
    }

    private static int ReadOffset(HttpContext context, Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue("offset", out var values))
        {
            return 0;
        }

        var raw = values.Count > 0 ? values[^1] : null;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            errors["offset"] = ["offset must be a nonnegative integer."];
            return 0;
        }

        return parsed;
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

    private static RecipeStatus? ReadOptionalStatus(
        HttpContext context,
        Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue("status", out var values))
        {
            return null;
        }

        var raw = values.Count > 0 ? values[^1] : null;
        if (EnumTextConverters.TryRecipeStatusFromText(raw, out var status))
        {
            return status;
        }

        errors["status"] = ["status must be one of normal, manual_only, or retired."];
        return null;
    }

    private static long? ReadOptionalId(
        HttpContext context,
        string name,
        Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue(name, out var values))
        {
            return null;
        }

        var raw = values.Count > 0 ? values[^1] : null;
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        errors[name] = [$"{name} must be a positive integer."];
        return null;
    }

    private static string ReadSort(HttpContext context, Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue("sort", out var values))
        {
            return "recently_added";
        }

        var raw = values.Count > 0 ? values[^1] : null;
        if (raw is not null && RecipeService.SortValues.Contains(raw, StringComparer.Ordinal))
        {
            return raw;
        }

        errors["sort"] = [$"sort must be one of {string.Join(", ", RecipeService.SortValues)}."];
        return "recently_added";
    }

    private static string? ReadSearch(HttpContext context, Dictionary<string, string[]> errors)
    {
        if (!context.Request.Query.TryGetValue("search", out var values))
        {
            return null;
        }

        var raw = values.Count > 0 ? values[^1] : null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalization = ReferenceDataNormalizer.Normalize(raw);
        return normalization switch
        {
            ReferenceDataNormalizationResult.Success success => success.Normalized,
            ReferenceDataNormalizationResult.Invalid
            {
                Reason: ReferenceDataNormalizationFailureReason.ControlCharacters,
            } => AddSearchError(errors),
            _ => null,
        };
    }

    private static string? AddSearchError(Dictionary<string, string[]> errors)
    {
        errors["search"] = ["search must not contain control characters."];
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
            ReadOptionalBoolean(root, "is_vegetarian", errors),
            ReadIngredientGroups(root, errors),
            ReadIngredients(root, "ingredients", "ingredients", errors),
            ReadInstructionSections(root, errors),
            ReadLongIds(root, "protein_ids", errors),
            ReadLongIds(root, "tag_ids", errors));

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
        && !request.IsVegetarian.IsSet
        && !request.IngredientGroups.IsSet
        && !request.Ingredients.IsSet
        && !request.InstructionSections.IsSet
        && !request.ProteinIds.IsSet
        && !request.TagIds.IsSet;

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

    private static RecipeField<IReadOnlyList<RecipeIngredientGroupWrite>> ReadIngredientGroups(
        JsonElement root,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty("ingredient_groups", out var element))
        {
            return default;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return RecipeField<IReadOnlyList<RecipeIngredientGroupWrite>>.Set([]);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors["ingredient_groups"] = ["ingredient_groups must be an array."];
            return default;
        }

        var groups = new List<RecipeIngredientGroupWrite>();
        var index = 0;
        foreach (var groupElement in element.EnumerateArray())
        {
            var key = $"ingredient_groups[{index}]";
            if (groupElement.ValueKind != JsonValueKind.Object)
            {
                errors[key] = ["Each ingredient group must be an object."];
                index++;
                continue;
            }

            var title = ReadNestedString(groupElement, "title", key, errors);
            var ingredients = ReadNestedIngredients(groupElement, "ingredients", key, errors);
            groups.Add(new RecipeIngredientGroupWrite(title, ingredients));
            index++;
        }

        return RecipeField<IReadOnlyList<RecipeIngredientGroupWrite>>.Set(groups);
    }

    private static RecipeField<IReadOnlyList<RecipeIngredientWrite>> ReadIngredients(
        JsonElement root,
        string property,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return default;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return RecipeField<IReadOnlyList<RecipeIngredientWrite>>.Set([]);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors[keyPrefix] = [$"{property} must be an array."];
            return default;
        }

        return RecipeField<IReadOnlyList<RecipeIngredientWrite>>.Set(
            ReadIngredientArray(element, keyPrefix, errors));
    }

    private static RecipeField<IReadOnlyList<RecipeInstructionSectionWrite>> ReadInstructionSections(
        JsonElement root,
        Dictionary<string, string[]> errors)
    {
        if (!root.TryGetProperty("instruction_sections", out var element))
        {
            return default;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return RecipeField<IReadOnlyList<RecipeInstructionSectionWrite>>.Set([]);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors["instruction_sections"] = ["instruction_sections must be an array."];
            return default;
        }

        var sections = new List<RecipeInstructionSectionWrite>();
        var index = 0;
        foreach (var sectionElement in element.EnumerateArray())
        {
            var key = $"instruction_sections[{index}]";
            if (sectionElement.ValueKind != JsonValueKind.Object)
            {
                errors[key] = ["Each instruction section must be an object."];
                index++;
                continue;
            }

            var title = ReadNestedString(sectionElement, "title", key, errors);
            var steps = ReadSteps(sectionElement, key, errors);
            sections.Add(new RecipeInstructionSectionWrite(title, steps));
            index++;
        }

        return RecipeField<IReadOnlyList<RecipeInstructionSectionWrite>>.Set(sections);
    }

    private static RecipeField<IReadOnlyList<long>> ReadLongIds(
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
            return RecipeField<IReadOnlyList<long>>.Set([]);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors[property] = [$"{property} must be an array of ids."];
            return default;
        }

        var ids = new List<long>();
        var index = 0;
        foreach (var idElement in element.EnumerateArray())
        {
            if (idElement.ValueKind == JsonValueKind.Number
                && idElement.TryGetInt64(out var id)
                && id > 0)
            {
                ids.Add(id);
            }
            else
            {
                errors[$"{property}[{index}]"] = ["Each id must be a positive integer."];
            }

            index++;
        }

        return RecipeField<IReadOnlyList<long>>.Set(ids);
    }

    private static string? ReadNestedString(
        JsonElement parent,
        string property,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        errors[$"{keyPrefix}.{property}"] = [$"{property} must be a string or null."];
        return null;
    }

    private static IReadOnlyList<RecipeIngredientWrite> ReadNestedIngredients(
        JsonElement parent,
        string property,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors[$"{keyPrefix}.{property}"] = [$"{property} must be an array."];
            return [];
        }

        return ReadIngredientArray(element, $"{keyPrefix}.{property}", errors);
    }

    private static IReadOnlyList<RecipeIngredientWrite> ReadIngredientArray(
        JsonElement array,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        var ingredients = new List<RecipeIngredientWrite>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var key = $"{keyPrefix}[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors[key] = ["Each ingredient must be an object."];
                index++;
                continue;
            }

            var ingredientId = ReadNestedLong(item, "ingredient_id", key, errors);
            var quantity = ReadNestedDecimal(item, "quantity", key, errors);
            var unitId = ReadNestedOptionalLong(item, "unit_id", key, errors);
            var preparationNote = ReadNestedString(item, "preparation_note", key, errors);
            var isOptional = ReadNestedBool(item, "is_optional", key, errors);
            ingredients.Add(new RecipeIngredientWrite(
                ingredientId,
                quantity,
                unitId,
                preparationNote,
                isOptional));
            index++;
        }

        return ingredients;
    }

    private static IReadOnlyList<string> ReadSteps(
        JsonElement section,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        if (!section.TryGetProperty("steps", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors[$"{keyPrefix}.steps"] = ["steps must be an array of strings."];
            return [];
        }

        var steps = new List<string>();
        var index = 0;
        foreach (var step in element.EnumerateArray())
        {
            if (step.ValueKind == JsonValueKind.String)
            {
                steps.Add(step.GetString()!);
            }
            else
            {
                errors[$"{keyPrefix}.steps[{index}]"] = ["Step text must be a string."];
            }

            index++;
        }

        return steps;
    }

    private static long ReadNestedLong(
        JsonElement parent,
        string property,
        string keyPrefix,
        Dictionary<string, string[]> errors,
        bool required = true)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                errors[$"{keyPrefix}.{property}"] = [$"{property} is required."];
            }

            return 0;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) && value > 0)
        {
            return value;
        }

        errors[$"{keyPrefix}.{property}"] = [$"{property} must be a positive integer."];
        return 0;
    }

    private static long? ReadNestedOptionalLong(
        JsonElement parent,
        string property,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) && value > 0)
        {
            return value;
        }

        errors[$"{keyPrefix}.{property}"] = [$"{property} must be a positive integer or null."];
        return null;
    }

    private static decimal? ReadNestedDecimal(
        JsonElement parent,
        string property,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value))
        {
            return value;
        }

        errors[$"{keyPrefix}.{property}"] = [$"{property} must be a number or null."];
        return null;
    }

    private static bool ReadNestedBool(
        JsonElement parent,
        string property,
        string keyPrefix,
        Dictionary<string, string[]> errors)
    {
        if (!parent.TryGetProperty(property, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        errors[$"{keyPrefix}.{property}"] = [$"{property} must be true or false."];
        return false;
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
