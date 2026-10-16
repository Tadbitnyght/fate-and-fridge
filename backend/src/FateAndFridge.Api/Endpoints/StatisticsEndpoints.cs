using System.Globalization;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
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
/// Statistics routes (docs/API.md §14, docs/M11_PLAN.md §2.1, §2.2). Reads are
/// gated by <c>recipes.view</c>; the single-user system actor and Admin have
/// full access and no new permission key is introduced.
/// </summary>
public static class StatisticsEndpoints
{
    public static IEndpointRouteBuilder MapStatisticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/statistics").WithTags("Statistics");

        group.MapGet("/overview", GetOverviewAsync).RequirePermission(PermissionKeys.RecipesView);

        return app;
    }

    private static async Task<IResult> GetOverviewAsync(
        StatisticsService statistics,
        CancellationToken cancellationToken)
    {
        var overview = await statistics.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(StatisticsOverviewResponse.From(overview));
    }
}
