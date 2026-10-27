using System.Globalization;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.LiveSync;
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

/// <summary>One household staple (docs/M8_PLAN.md §1.9, §2.2).</summary>
public sealed record GroceryStapleResponse(
    long Id,
    string Name,
    long? IngredientId,
    long? DepartmentId,
    decimal? DefaultQuantityNumeric,
    long? UnitId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static GroceryStapleResponse From(GroceryStapleRecord staple) =>
        new(
            staple.Id,
            staple.Name,
            staple.IngredientId,
            staple.DepartmentId,
            staple.DefaultQuantityNumeric,
            staple.UnitId,
            staple.IsActive,
            staple.CreatedAt,
            staple.UpdatedAt);
}

/// <summary>The household staple catalog document (docs/M8_PLAN.md §2.2).</summary>
public sealed record GroceryStaplesResponse(IReadOnlyList<GroceryStapleResponse> Staples);

/// <summary>The household-shared check state of one line (docs/M8_PLAN.md §1.10, §2.2).</summary>
public sealed record GroceryCheckStateResponse(
    string Identity,
    bool Checked,
    DateTimeOffset? CheckedAt,
    long? CheckedByUserId)
{
    public static GroceryCheckStateResponse From(GroceryCheckStateRecord state) =>
        new(state.Identity, state.Checked, state.CheckedAt, state.CheckedByUserId);
}

/// <summary>
/// Grocery routes (docs/M8_PLAN.md §2.2). M8-03 delivers the read projection,
/// M8-04 adds range-scoped manual items plus the caller's preferences, M8-05 adds
/// the household staple catalog, and M8-06 adds the household-shared check state.
/// Reads are gated by <c>groceries.view</c> and writes by <c>groceries.edit</c>;
/// the single-user system actor has full access.
/// </summary>
public static class GroceryEndpoints
{
    private const string ManualItemNotFoundMessage =
        "The requested manual grocery item does not exist.";

    private const string StapleNotFoundMessage =
        "The requested grocery staple does not exist or is inactive.";

    private const string StaplePatchNotFoundMessage =
        "The requested grocery staple does not exist.";

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

        // Staples are a household-wide reusable catalog that is never auto-added;
        // reads use groceries.view and writes groceries.edit (docs/M8_PLAN.md §1.9, §2.2).
        group.MapGet("/staples", GetStaplesAsync).RequirePermission(PermissionKeys.GroceriesView);
        group.MapPost("/staples", CreateStapleAsync).RequirePermission(PermissionKeys.GroceriesEdit);
        group.MapPatch("/staples/{id:long}", UpdateStapleAsync).RequirePermission(PermissionKeys.GroceriesEdit);
        group.MapPost("/staples/{id:long}/add-to-list", AddStapleToListAsync)
            .RequirePermission(PermissionKeys.GroceriesEdit);

        // Household-shared check state keyed by the stable GroceryItemIdentity
        // hash (docs/M8_PLAN.md §1.10, §2.2).
        group.MapPost("/check", CheckAsync).RequirePermission(PermissionKeys.GroceriesEdit);
        group.MapPost("/uncheck", UncheckAsync).RequirePermission(PermissionKeys.GroceriesEdit);

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
        ILiveSyncPublisher liveSync,
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
            .CreateAsync(
                validation.Request,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome is GroceryManualItemOutcome.Success created)
        {
            await liveSync.GroceriesUpdatedAsync(actorUserId, created.Item.Id, cancellationToken);
        }

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
        ILiveSyncPublisher liveSync,
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
            .UpdateAsync(
                id,
                validation.Patch,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome is GroceryManualItemOutcome.Success updated)
        {
            await liveSync.GroceriesUpdatedAsync(actorUserId, updated.Item.Id, cancellationToken);
        }

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
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryManualItemService manualItems,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var (actorUserId, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var outcome = await manualItems
            .DeleteAsync(id, actorUserId, CalendarEndpoints.RemoteIpAddress(context), cancellationToken)
            .ConfigureAwait(false);
        if (outcome is GroceryManualItemDeleteOutcome.Deleted)
        {
            await liveSync.GroceriesUpdatedAsync(actorUserId, id, cancellationToken);
        }

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

    // ---- Staples (M8-05) -------------------------------------------------

    private static async Task<IResult> GetStaplesAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryStapleService staples,
        CancellationToken cancellationToken)
    {
        var (_, actorError) = await CalendarEndpoints
            .ResolveActorAsync(actorProvider, cancellationToken)
            .ConfigureAwait(false);
        if (actorError is not null)
        {
            return actorError;
        }

        var includeInactive = false;
        if (ReadQuery(context, "include_inactive") is { } raw)
        {
            if (!bool.TryParse(raw, out includeInactive))
            {
                return ApiResults.ValidationError(
                    FieldError("include_inactive", "include_inactive must be true or false."));
            }
        }

        var catalog = await staples.ListAsync(includeInactive, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new GroceryStaplesResponse(
            catalog.Select(GroceryStapleResponse.From).ToArray()));
    }

    private static async Task<IResult> CreateStapleAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryStapleService staples,
        ILiveSyncPublisher liveSync,
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

        GroceryStapleCreateValidation validation;
        using (var document = body.Document!)
        {
            validation = GroceryStaplePayloadValidator.ValidateCreate(document.RootElement);
        }

        if (validation.Request is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var outcome = await staples
            .CreateAsync(
                validation.Request,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome is GroceryStapleOutcome.Success created)
        {
            await liveSync.GroceriesUpdatedAsync(actorUserId, created.Staple.Id, cancellationToken);
        }

        return outcome switch
        {
            GroceryStapleOutcome.Success success => Results.Json(
                GroceryStapleResponse.From(success.Staple),
                statusCode: StatusCodes.Status201Created),
            GroceryStapleOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            GroceryStapleOutcome.StapleExists exists => StapleExists(exists.ExistingId),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> UpdateStapleAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryStapleService staples,
        ILiveSyncPublisher liveSync,
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

        GroceryStaplePatchValidation validation;
        using (var document = body.Document!)
        {
            validation = GroceryStaplePayloadValidator.ValidatePatch(document.RootElement);
        }

        if (validation.Patch is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var outcome = await staples
            .UpdateAsync(
                id,
                validation.Patch,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome is GroceryStapleOutcome.Success updated)
        {
            await liveSync.GroceriesUpdatedAsync(actorUserId, updated.Staple.Id, cancellationToken);
        }

        return outcome switch
        {
            GroceryStapleOutcome.Success success =>
                Results.Ok(GroceryStapleResponse.From(success.Staple)),
            GroceryStapleOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            GroceryStapleOutcome.StapleExists exists => StapleExists(exists.ExistingId),
            GroceryStapleOutcome.NotFound => StaplePatchNotFound(),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> AddStapleToListAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryStapleService staples,
        ILiveSyncPublisher liveSync,
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

        GroceryStapleAddToListValidation validation;
        using (var document = body.Document!)
        {
            validation = GroceryStaplePayloadValidator.ValidateAddToList(document.RootElement);
        }

        if (validation.Request is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var outcome = await staples
            .AddToListAsync(
                id,
                validation.Request,
                actorUserId,
                CalendarEndpoints.RemoteIpAddress(context),
                cancellationToken)
            .ConfigureAwait(false);
        if (outcome is GroceryStapleAddOutcome.Success added)
        {
            await liveSync.GroceriesUpdatedAsync(actorUserId, added.Item.Id, cancellationToken);
        }

        return outcome switch
        {
            GroceryStapleAddOutcome.Success success => Results.Json(
                GroceryManualItemResponse.From(success.Item),
                statusCode: StatusCodes.Status201Created),
            GroceryStapleAddOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
            GroceryStapleAddOutcome.NotFound => StapleNotFound(),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
        };
    }

    // ---- Check state (M8-06) ---------------------------------------------

    private static Task<IResult> CheckAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryCheckStateService checkState,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken) =>
        SetCheckedAsync(context, actorProvider, checkState, isChecked: true, liveSync, cancellationToken);

    private static Task<IResult> UncheckAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryCheckStateService checkState,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken) =>
        SetCheckedAsync(context, actorProvider, checkState, isChecked: false, liveSync, cancellationToken);

    private static async Task<IResult> SetCheckedAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        GroceryCheckStateService checkState,
        bool isChecked,
        ILiveSyncPublisher liveSync,
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

        GroceryCheckPayloadValidation validation;
        using (var document = body.Document!)
        {
            validation = GroceryCheckPayloadValidator.Validate(document.RootElement);
        }

        if (validation.Identity is null)
        {
            return ApiResults.ValidationError(validation.FieldErrors);
        }

        var state = await checkState
            .SetCheckedAsync(validation.Identity, isChecked, actorUserId, cancellationToken)
            .ConfigureAwait(false);
        await liveSync.GroceriesUpdatedAsync(actorUserId, state.Identity, cancellationToken);
        return Results.Ok(GroceryCheckStateResponse.From(state));
    }

    private static IResult ManualItemNotFound() =>
        ApiResults.Error(
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            ManualItemNotFoundMessage);

    private static IResult StapleNotFound() =>
        ApiResults.Error(
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            StapleNotFoundMessage);

    private static IResult StaplePatchNotFound() =>
        ApiResults.Error(
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            StaplePatchNotFoundMessage);

    private static IResult StapleExists(long existingId) =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.GroceryStapleExists,
            "An active staple with that normalized name already exists.",
            new Dictionary<string, object?> { ["existing_id"] = existingId });

    private static IReadOnlyDictionary<string, string[]> FieldError(string field, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] };

    private static string? ReadQuery(HttpContext context, string field)
    {
        if (!context.Request.Query.TryGetValue(field, out var values) || values.Count == 0)
        {
            return null;
        }

        return values[^1];
    }
}
