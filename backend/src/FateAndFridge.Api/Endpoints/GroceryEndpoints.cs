using System.Globalization;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Resolved grocery range document (docs/M8_PLAN.md §2.2).</summary>
public sealed record GroceryRangeResponse(string Preset, string Start, string End, int DayCount)
{
    public static GroceryRangeResponse From(GroceryRangeResolution range) =>
        new(
            range.Preset,
            range.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range.DayCount);
}

/// <summary>One contributing meal row for a recipe-derived line.</summary>
public sealed record GroceryItemSourceResponse(long RecipeId, string RecipeName, string Date)
{
    public static GroceryItemSourceResponse From(GroceryItemSource source) =>
        new(
            source.RecipeId,
            source.RecipeName,
            source.MealDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>One grocery line (docs/M8_PLAN.md §2.2).</summary>
public sealed record GroceryItemResponse(
    string Identity,
    string Name,
    string QuantityText,
    decimal? Quantity,
    string? Unit,
    long? IngredientId,
    long? DepartmentId,
    bool Checked,
    string SourceType,
    long? ManualItemId,
    IReadOnlyList<GroceryItemSourceResponse> Sources)
{
    public static GroceryItemResponse From(GroceryListItemRecord item) =>
        new(
            item.Identity,
            item.Name,
            item.QuantityText,
            item.Quantity,
            item.Unit,
            item.IngredientId,
            item.DepartmentId,
            item.Checked,
            item.SourceType,
            item.ManualItemId,
            item.Sources.Select(GroceryItemSourceResponse.From).ToArray());
}

/// <summary>One department bucket of ordered grocery lines.</summary>
public sealed record GroceryDepartmentResponse(
    long? DepartmentId,
    string Name,
    int SortOrder,
    bool IsOther,
    IReadOnlyList<GroceryItemResponse> Items)
{
    public static GroceryDepartmentResponse From(GroceryDepartmentRecord department) =>
        new(
            department.DepartmentId,
            department.Name,
            department.SortOrder,
            department.IsOther,
            department.Items.Select(GroceryItemResponse.From).ToArray());
}

/// <summary>Full grocery list document (docs/M8_PLAN.md §2.2).</summary>
public sealed record GroceryListResponse(
    GroceryRangeResponse Range,
    IReadOnlyList<GroceryDepartmentResponse> Departments)
{
    public static GroceryListResponse From(GroceryListRecord list) =>
        new(
            GroceryRangeResponse.From(list.Range),
            list.Departments.Select(GroceryDepartmentResponse.From).ToArray());
}

/// <summary>
/// One stored manual grocery item (docs/M8_PLAN.md §1.8, §2.2). Dates are
/// household-local <c>YYYY-MM-DD</c>; timestamps are UTC.
/// </summary>
public sealed record GroceryManualItemResponse(
    long Id,
    string Name,
    decimal? QuantityNumeric,
    long? UnitId,
    long? IngredientId,
    long? DepartmentId,
    string RangeStart,
    int RangeDays,
    bool SaveForFuture,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static GroceryManualItemResponse From(GroceryManualItemRecord item) =>
        new(
            item.Id,
            item.Name,
            item.QuantityNumeric,
            item.UnitId,
            item.IngredientId,
            item.DepartmentId,
            item.RangeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            item.RangeDays,
            item.SaveForFuture,
            item.CreatedAt,
            item.UpdatedAt);
}

/// <summary>Caller grocery preferences (docs/M8_PLAN.md §1.7, §1.11, §2.2).</summary>
public sealed record GroceryPreferenceResponse(
    bool HideChecked,
    string? RangePreset,
    string? RangeStart,
    int? RangeDays)
{
    public static GroceryPreferenceResponse From(GroceryPreferenceSnapshot preference) =>
        new(
            preference.HideChecked,
            preference.RangePreset,
            preference.RangeStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            preference.RangeDays);
}

/// <summary>
/// Grocery routes (docs/M8_PLAN.md §2.2). M8-03 delivers the read projection and
/// M8-04 adds range-scoped manual items plus the caller's preferences. Reads are
/// gated by <c>groceries.view</c> and manual-item writes by <c>groceries.edit</c>;
/// the single-user system actor has full access. Later M8 issues add staples and
/// check state under the same group.
/// </summary>
public static class GroceryEndpoints
{
    private const string ManualItemNotFoundMessage =
        "The requested manual grocery item does not exist.";

    public static IEndpointRouteBuilder MapGroceryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/groceries").WithTags("Groceries");

        group.MapGet("", GetListAsync).RequirePermission(PermissionKeys.GroceriesView);

        // Manual items are range-scoped free text with optional quantity/unit and
        // an optional canonical ingredient (docs/M8_PLAN.md §1.8, §2.2).
        group.MapPost("/manual", CreateManualItemAsync).RequirePermission(PermissionKeys.GroceriesEdit);
        group.MapPatch("/manual/{id:long}", UpdateManualItemAsync)
            .RequirePermission(PermissionKeys.GroceriesEdit);
        group.MapDelete("/manual/{id:long}", DeleteManualItemAsync)
            .RequirePermission(PermissionKeys.GroceriesEdit);

        // Preferences are per-user hide-checked plus the remembered range; both
        // read and write use groceries.view (docs/M8_PLAN.md §2.2).
        group.MapGet("/preferences", GetPreferencesAsync).RequirePermission(PermissionKeys.GroceriesView);
        group.MapPatch("/preferences", UpdatePreferencesAsync).RequirePermission(PermissionKeys.GroceriesView);

        return app;
    }

    private static async Task<IResult> GetListAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryListService groceryList,
        CancellationToken cancellationToken)
    {
        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var request = new GroceryRangeRequest(
            ReadQuery(context, "preset"),
            ReadQuery(context, "start"),
            ReadQuery(context, "days"));

        var outcome = await groceryList
            .GetListAsync(request, actorUserId, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            GroceryListOutcome.Success success => Results.Ok(GroceryListResponse.From(success.List)),
            GroceryListOutcome.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            GroceryListOutcome.RangeTooLarge => ApiResults.Error(
                StatusCodes.Status400BadRequest,
                ErrorCodes.GroceryRangeTooLarge,
                "The requested grocery range may span at most 366 days."),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> CreateManualItemAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryManualItemService manualItems,
        CancellationToken cancellationToken)
    {
        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        GroceryManualItemCreateValidation validation;
        using (var document = body.Document!)
        {
            validation = GroceryManualItemPayloadValidator.ValidateCreate(document.RootElement);
        }

        if (validation.Request is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var outcome = await manualItems
            .CreateAsync(validation.Request, actorUserId, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            GroceryManualItemOutcome.Success success => Results.Json(
                GroceryManualItemResponse.From(success.Item),
                statusCode: StatusCodes.Status201Created),
            GroceryManualItemOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> UpdateManualItemAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryManualItemService manualItems,
        CancellationToken cancellationToken)
    {
        var (_, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        GroceryManualItemPatchValidation validation;
        using (var document = body.Document!)
        {
            validation = GroceryManualItemPayloadValidator.ValidatePatch(document.RootElement);
        }

        if (validation.Patch is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var outcome = await manualItems
            .UpdateAsync(id, validation.Patch, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            GroceryManualItemOutcome.Success success =>
                Results.Ok(GroceryManualItemResponse.From(success.Item)),
            GroceryManualItemOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            GroceryManualItemOutcome.NotFound => ManualItemNotFound(),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> DeleteManualItemAsync(
        long id,
        ICurrentActorProvider actorProvider,
        GroceryManualItemService manualItems,
        CancellationToken cancellationToken)
    {
        var (_, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var outcome = await manualItems.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        return outcome switch
        {
            GroceryManualItemDeleteOutcome.Deleted => Results.NoContent(),
            _ => ManualItemNotFound(),
        };
    }

    private static async Task<IResult> GetPreferencesAsync(
        ICurrentActorProvider actorProvider,
        GroceryPreferenceService preferences,
        CancellationToken cancellationToken)
    {
        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var snapshot = await preferences.GetAsync(actorUserId, cancellationToken).ConfigureAwait(false);
        return Results.Ok(GroceryPreferenceResponse.From(snapshot));
    }

    private static async Task<IResult> UpdatePreferencesAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryPreferenceService preferences,
        CancellationToken cancellationToken)
    {
        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        GroceryPreferencePatchValidation validation;
        using (var document = body.Document!)
        {
            validation = GroceryPreferencePayloadValidator.Validate(document.RootElement);
        }

        if (validation.Patch is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var outcome = await preferences
            .PatchAsync(actorUserId, validation.Patch, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            GroceryPreferenceOutcome.Success success =>
                Results.Ok(GroceryPreferenceResponse.From(success.Preference)),
            GroceryPreferenceOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static IResult ManualItemNotFound() =>
        ApiResults.Error(
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            ManualItemNotFoundMessage);

    private static string? ReadQuery(HttpContext context, string field)
    {
        if (!context.Request.Query.TryGetValue(field, out var values) || values.Count == 0)
        {
            return null;
        }

        return values[^1];
    }
}
