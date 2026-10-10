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
/// Grocery routes (docs/M8_PLAN.md §2.2). M8-03 delivers the read projection;
/// reads are gated by <c>groceries.view</c> and the single-user system actor has
/// full access. Later M8 issues add manual items, staples, check state, and
/// preferences under the same group.
/// </summary>
public static class GroceryEndpoints
{
    public static IEndpointRouteBuilder MapGroceryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/groceries").WithTags("Groceries");

        group.MapGet("", GetListAsync).RequirePermission(PermissionKeys.GroceriesView);

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

    private static string? ReadQuery(HttpContext context, string field)
    {
        if (!context.Request.Query.TryGetValue(field, out var values) || values.Count == 0)
        {
            return null;
        }

        return values[^1];
    }
}
