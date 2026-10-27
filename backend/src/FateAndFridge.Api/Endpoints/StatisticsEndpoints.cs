using System.Globalization;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>One household-local statistics window (docs/M11_PLAN.md §2.2).</summary>
public sealed record StatisticsWindowResponse(string Label, string Start, string End)
{
    public static StatisticsWindowResponse From(StatisticsWindowRecord window) =>
        new(
            window.Label,
            window.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            window.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>The household-local window labels the overview was computed for.</summary>
public sealed record StatisticsWindowsResponse(
    string Today,
    string Timezone,
    StatisticsWindowResponse Month,
    StatisticsWindowResponse Year);

/// <summary>
/// One ranked recipe (docs/M11_PLAN.md §2.2). <c>recipe_id</c> is null for a
/// tombstone; <c>status</c> carries the badge (<c>normal</c>, <c>manual_only</c>,
/// <c>retired</c>, or <c>deleted</c>).
/// </summary>
public sealed record RankedRecipeResponse(
    long? RecipeId,
    string Name,
    string Status,
    int TimesEaten,
    string LastEaten)
{
    public static RankedRecipeResponse From(RankedRecipeRecord recipe) =>
        new(
            recipe.RecipeId,
            recipe.Name,
            recipe.Status,
            recipe.TimesEaten,
            recipe.LastEaten.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>One protein bar in the primary-protein distribution.</summary>
public sealed record ProteinCountResponse(long ProteinId, string Name, int Count)
{
    public static ProteinCountResponse From(ProteinCountRecord protein) =>
        new(protein.ProteinId, protein.Name, protein.Count);
}

/// <summary>
/// Recipe occurrences grouped per primary protein, with an explicit
/// <c>no_primary_protein</c> figure (docs/M11_PLAN.md §1.12).
/// </summary>
public sealed record ProteinDistributionResponse(
    IReadOnlyList<ProteinCountResponse> Proteins,
    int NoPrimaryProtein)
{
    public static ProteinDistributionResponse From(ProteinDistributionRecord distribution) =>
        new(
            distribution.Proteins.Select(ProteinCountResponse.From).ToArray(),
            distribution.NoPrimaryProtein);
}

/// <summary>One special-event count over the household calendar year.</summary>
public sealed record SpecialEventCountResponse(string EntryType, int Count)
{
    public static SpecialEventCountResponse From(SpecialEventCountRecord count) =>
        new(EnumTextConverters.CalendarEntryTypeToText(count.EntryType), count.Count);
}

/// <summary>The household statistics overview (docs/M11_PLAN.md §2.2).</summary>
public sealed record StatisticsOverviewResponse(
    StatisticsWindowsResponse Windows,
    IReadOnlyList<RankedRecipeResponse> MostEaten,
    IReadOnlyList<RankedRecipeResponse> LeastRecentlyEaten,
    int NeverEaten,
    int MealCountMonth,
    int MealCountYear,
    ProteinDistributionResponse ProteinDistribution,
    IReadOnlyList<SpecialEventCountResponse> SpecialEventCounts)
{
    public static StatisticsOverviewResponse From(StatisticsOverviewRecord overview) =>
        new(
            new StatisticsWindowsResponse(
                overview.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                overview.Timezone,
                StatisticsWindowResponse.From(overview.Month),
                StatisticsWindowResponse.From(overview.Year)),
            overview.MostEaten.Select(RankedRecipeResponse.From).ToArray(),
            overview.LeastRecentlyEaten.Select(RankedRecipeResponse.From).ToArray(),
            overview.NeverEaten,
            overview.MealCountMonth,
            overview.MealCountYear,
            ProteinDistributionResponse.From(overview.ProteinDistribution),
            overview.SpecialEventCounts.Select(SpecialEventCountResponse.From).ToArray());
}

/// <summary>
/// The per-recipe metrics (docs/API.md §14, docs/PRODUCT_SPEC.md §12,
/// docs/M11_PLAN.md §1.2, §1.21): <c>last_eaten</c> and <c>next_planned</c> are
/// household-local dates (<c>yyyy-MM-dd</c>) or null, and <c>times_eaten</c> is
/// the historical occurrence count or null when there is none — never a fake
/// zero.
/// </summary>
public sealed record RecipeMetricsResponse(
    string? LastEaten,
    string? NextPlanned,
    int? TimesEaten)
{
    public static RecipeMetricsResponse From(RecipeMetricsRecord metrics) =>
        new(
            metrics.LastEaten?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            metrics.NextPlanned?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            metrics.TimesEaten);
}

/// <summary>The applied household-local history range (docs/M11_PLAN.md §1.26).</summary>
public sealed record MealHistoryRangeResponse(string Start, string End, string Timezone)
{
    public static MealHistoryRangeResponse From(MealHistoryRangeRecord range) =>
        new(
            range.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            range.Timezone);
}

/// <summary>One history page: the month key, its label, and its calendar bounds.</summary>
public sealed record MealHistoryMonthResponse(string Key, string Label, string Start, string End)
{
    public static MealHistoryMonthResponse From(MealHistoryMonthRecord month) =>
        new(
            month.Key,
            month.Label,
            month.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            month.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>
/// One day of the meal log (docs/M11_PLAN.md §1.26): the date, entry type, the
/// recipe id (null for a tombstone), the display name, and the primary protein
/// name (null when the entry carries no recipe or its recipe lists no protein).
/// </summary>
public sealed record MealHistoryDayResponse(
    string Date,
    string EntryType,
    long? RecipeId,
    string Name,
    string? Protein)
{
    public static MealHistoryDayResponse From(MealHistoryDayRecord day) =>
        new(
            day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            EnumTextConverters.CalendarEntryTypeToText(day.EntryType),
            day.RecipeId,
            day.Name,
            day.Protein);
}

/// <summary>
/// The month cursors, one month per step inside the applied range
/// (docs/M11_PLAN.md §1.26): <c>newer</c> is the next later month, <c>older</c>
/// the next earlier one, and both are null at the range ends.
/// </summary>
public sealed record MealHistoryPagingResponse(string? Newer, string? Older)
{
    public static MealHistoryPagingResponse From(MealHistoryPagingRecord paging) =>
        new(paging.Newer, paging.Older);
}

/// <summary>One month page of the strictly-past meal log (docs/M11_PLAN.md §2.2).</summary>
public sealed record MealHistoryResponse(
    MealHistoryRangeResponse Range,
    MealHistoryMonthResponse Month,
    IReadOnlyList<MealHistoryDayResponse> Days,
    MealHistoryPagingResponse Paging)
{
    public static MealHistoryResponse From(MealHistoryRecord history) =>
        new(
            MealHistoryRangeResponse.From(history.Range),
            MealHistoryMonthResponse.From(history.Month),
            history.Days.Select(MealHistoryDayResponse.From).ToArray(),
            MealHistoryPagingResponse.From(history.Paging));
}

/// <summary>
/// Statistics routes (docs/API.md §14, docs/M11_PLAN.md §2.1, §2.2). The
/// overview and per-recipe reads are gated by <c>recipes.view</c> and the
/// meal-history read by <c>calendar.view</c>; the single-user system actor and
/// Admin have full access and no new permission key is introduced.
/// </summary>
public static class StatisticsEndpoints
{
    public static IEndpointRouteBuilder MapStatisticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/statistics").WithTags("Statistics");

        group.MapGet("/overview", GetOverviewAsync).RequirePermission(PermissionKeys.RecipesView);
        group.MapGet("/recipes/{id:long}", GetRecipeMetricsAsync)
            .RequirePermission(PermissionKeys.RecipesView);
        group.MapGet("/history", GetHistoryAsync).RequirePermission(PermissionKeys.CalendarView);

        return app;
    }

    private static async Task<IResult> GetOverviewAsync(
        StatisticsService statistics,
        CancellationToken cancellationToken)
    {
        var overview = await statistics.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(StatisticsOverviewResponse.From(overview));
    }

    private static async Task<IResult> GetRecipeMetricsAsync(
        long id,
        StatisticsService statistics,
        CancellationToken cancellationToken)
    {
        var metrics = await statistics.GetRecipeMetricsAsync(id, cancellationToken)
            .ConfigureAwait(false);
        return metrics is null
            ? ApiResults.Error(
                StatusCodes.Status404NotFound,
                ErrorCodes.NotFound,
                "The recipe does not exist.")
            : Results.Ok(RecipeMetricsResponse.From(metrics));
    }

    private static async Task<IResult> GetHistoryAsync(
        HttpContext context,
        MealHistoryService history,
        CancellationToken cancellationToken)
    {
        var request = new MealHistoryRequest(
            ReadQuery(context, "start"),
            ReadQuery(context, "end"),
            ReadQuery(context, "month"),
            ReadQuery(context, "entry_type"),
            ReadQuery(context, "recipe"),
            ReadQuery(context, "protein"));

        var outcome = await history.GetHistoryAsync(request, cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            MealHistoryOutcome.Success success =>
                Results.Ok(MealHistoryResponse.From(success.History)),
            MealHistoryOutcome.ValidationFailed failed =>
                ApiResults.ValidationError(failed.FieldErrors),
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
