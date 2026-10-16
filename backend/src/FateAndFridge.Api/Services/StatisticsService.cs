using System.Globalization;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>One household-local statistics window (label plus inclusive bounds).</summary>
public sealed record StatisticsWindowRecord(string Label, DateOnly Start, DateOnly End);

/// <summary>
/// One ranked recipe in <c>most_eaten</c> / <c>least_recently_eaten</c>
/// (docs/M11_PLAN.md §2.2). <see cref="RecipeId"/> is null only for a tombstone
/// (a hard-deleted recipe, whose meals still count under the stored snapshot
/// name); <see cref="Status"/> is one of <c>normal</c>, <c>manual_only</c>,
/// <c>retired</c>, or <c>deleted</c>.
/// </summary>
public sealed record RankedRecipeRecord(
    long? RecipeId,
    string Name,
    string Status,
    int TimesEaten,
    DateOnly LastEaten);

/// <summary>One protein bar in the primary-protein distribution.</summary>
public sealed record ProteinCountRecord(long ProteinId, string Name, int Count);

/// <summary>
/// Recipe occurrences with a primary protein grouped per protein, plus the
/// explicit count of occurrences without one (docs/M11_PLAN.md §1.12).
/// </summary>
public sealed record ProteinDistributionRecord(
    IReadOnlyList<ProteinCountRecord> Proteins,
    int NoPrimaryProtein);

/// <summary>One special-event count over the household calendar year.</summary>
public sealed record SpecialEventCountRecord(CalendarEntryType EntryType, int Count);

/// <summary>The household statistics overview (docs/M11_PLAN.md §2.2).</summary>
public sealed record StatisticsOverviewRecord(
    DateOnly Today,
    string Timezone,
    StatisticsWindowRecord Month,
    StatisticsWindowRecord Year,
    IReadOnlyList<RankedRecipeRecord> MostEaten,
    IReadOnlyList<RankedRecipeRecord> LeastRecentlyEaten,
    int NeverEaten,
    int MealCountMonth,
    int MealCountYear,
    ProteinDistributionRecord ProteinDistribution,
    IReadOnlyList<SpecialEventCountRecord> SpecialEventCounts);

/// <summary>
/// The on-demand statistics aggregate reads (docs/M11_PLAN.md §1.14, §2.1).
/// Every metric is computed from <c>calendar_entries</c> (plus the recipe and
/// protein joins) on each request: no cache, no statistics table, and no
/// background job. The service is scoped over the request
/// <see cref="AppDbContext"/> and only reads.
/// </summary>
/// <remarks>
/// Occurrence eligibility and the eaten/planned split come from
/// <see cref="MealOccurrenceRules"/> so this overview, the per-recipe metrics
/// (M11-02), and the meal-history browser (M11-03) cannot drift. "Eaten" is
/// strictly before the household today; today's dinner is not eaten
/// (docs/M11_PLAN.md §1.3). Rankings group by <c>recipe_id</c> when present, else
/// the normalized stored snapshot name, so a retired or hard-deleted recipe's
/// meals keep counting (docs/M11_PLAN.md §1.5, §1.20).
/// </remarks>
public sealed class StatisticsService(AppDbContext dbContext, HouseholdClock clock)
{
    /// <summary>How many ranked recipes each of the two rankings returns (§1.19).</summary>
    public const int RankingSize = 10;

    /// <summary>Status badge for a tombstone (a hard-deleted recipe).</summary>
    private const string DeletedStatus = "deleted";

    /// <summary>
    /// The seven special-event types counted for the household calendar year
    /// (docs/M11_PLAN.md §1.13). The plain <c>recipe</c> type is covered by the
    /// meal counts instead.
    /// </summary>
    private static readonly CalendarEntryType[] SpecialEventTypes =
    [
        CalendarEntryType.EatOut,
        CalendarEntryType.HostFamilyDinner,
        CalendarEntryType.GoToFamilyDinner,
        CalendarEntryType.Holiday,
        CalendarEntryType.Misc,
        CalendarEntryType.FendForYourself,
        CalendarEntryType.Vacation,
    ];

    /// <summary>
    /// English month names, table-driven so the window label is deterministic
    /// under globalization-invariant mode (invariant ICU is disabled on the
    /// deployment host).
    /// </summary>
    private static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private readonly AppDbContext _dbContext = dbContext;
    private readonly HouseholdClock _clock = clock;

    /// <summary>
    /// Builds the household overview: all-time rankings, the household calendar
    /// month/year meal counts, the primary-protein distribution, and the
    /// special-event counts (docs/M11_PLAN.md §2.2).
    /// </summary>
    public async Task<StatisticsOverviewRecord> GetOverviewAsync(
        CancellationToken cancellationToken = default)
    {
        var context = await _clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var today = context.Today;
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var yearStart = new DateOnly(today.Year, 1, 1);
        var yearEnd = new DateOnly(today.Year, 12, 31);

        // 1) Eaten occurrences: past recipe-carrying entries only.
        var eaten = await _dbContext.CalendarEntries
            .AsNoTracking()
            .Where(MealOccurrenceRules.IsRecipeOccurrence)
            .Where(MealOccurrenceRules.EatenBefore(today))
            .Select(entry => new EatenOccurrence(
                entry.RecipeId,
                entry.SnapshotRecipeName,
                entry.MealDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 2) Live recipes: ranking names/status badges plus the never-eaten count.
        var recipes = await _dbContext.Recipes
            .AsNoTracking()
            .Select(recipe => new RecipeSummary(recipe.Id, recipe.Name, recipe.Status))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var recipeById = recipes.ToDictionary(recipe => recipe.Id);

        var groups = BuildGroups(eaten, recipeById);

        // Most eaten: count desc, deterministic name/id tie-break, top 10 (§1.19).
        var mostEaten = groups
            .OrderByDescending(group => group.TimesEaten)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ThenBy(group => group.RecipeId ?? long.MaxValue)
            .Take(RankingSize)
            .Select(group => group.ToRecord())
            .ToArray();

        // Least recently eaten: oldest last-eaten first, only recipes eaten at
        // least once, top 10 (§1.19).
        var leastRecentlyEaten = groups
            .OrderBy(group => group.LastEaten)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ThenBy(group => group.RecipeId ?? long.MaxValue)
            .Take(RankingSize)
            .Select(group => group.ToRecord())
            .ToArray();

        var eatenLiveRecipeIds = eaten
            .Where(occurrence => occurrence.RecipeId is not null)
            .Select(occurrence => occurrence.RecipeId!.Value)
            .ToHashSet();
        var neverEaten = recipes.Count(recipe => !eatenLiveRecipeIds.Contains(recipe.Id));

        // 3) Meal counts: every entry type in the household calendar month/year
        // counts, so one query over the year's dates serves both (§1.24).
        var yearDates = await _dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.MealDate >= yearStart && entry.MealDate <= yearEnd)
            .Select(entry => entry.MealDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 4) Special-event counts: one per type over the household calendar year.
        var eventCounts = await _dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.MealDate >= yearStart && entry.MealDate <= yearEnd)
            .GroupBy(entry => entry.EntryType)
            .Select(group => new { EntryType = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var eventCountByType = eventCounts.ToDictionary(row => row.EntryType, row => row.Count);
        var specialEventCounts = SpecialEventTypes
            .Select(type => new SpecialEventCountRecord(
                type,
                eventCountByType.TryGetValue(type, out var count) ? count : 0))
            .ToArray();

        var proteinDistribution = await BuildProteinDistributionAsync(eaten, cancellationToken)
            .ConfigureAwait(false);

        return new StatisticsOverviewRecord(
            today,
            context.TimeZone.Id,
            new StatisticsWindowRecord(MonthLabel(today), monthStart, monthEnd),
            new StatisticsWindowRecord(YearLabel(today), yearStart, yearEnd),
            mostEaten,
            leastRecentlyEaten,
            neverEaten,
            yearDates.Count(date => date >= monthStart),
            yearDates.Count,
            proteinDistribution,
            specialEventCounts);
    }

    private static List<RecipeGroup> BuildGroups(
        IReadOnlyList<EatenOccurrence> eaten,
        IReadOnlyDictionary<long, RecipeSummary> recipeById)
    {
        var groups = new Dictionary<string, RecipeGroup>(StringComparer.Ordinal);
        foreach (var occurrence in eaten)
        {
            var key = MealOccurrenceRules.GroupKey(occurrence.RecipeId, occurrence.SnapshotRecipeName);
            if (key is null)
            {
                // Unreachable for M5-derived entries (IncludeGroceries implies a
                // recipe id or a snapshot name); skip the ranking rather than
                // invent an identity. The meal still counts in meal_count_*.
                continue;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = new RecipeGroup(occurrence.RecipeId);
                groups[key] = group;
            }

            group.TimesEaten++;
            if (occurrence.MealDate > group.LastEaten)
            {
                group.LastEaten = occurrence.MealDate;
            }

            // Tombstone display name: the most recent snapshot spelling, with a
            // deterministic ordinal tie-break for equal dates.
            var display = MealOccurrenceRules.SnapshotDisplayName(occurrence.SnapshotRecipeName);
            if (display is not null
                && (group.SnapshotName is null
                    || occurrence.MealDate > group.SnapshotDate
                    || (occurrence.MealDate == group.SnapshotDate
                        && string.CompareOrdinal(display, group.SnapshotName) < 0)))
            {
                group.SnapshotDate = occurrence.MealDate;
                group.SnapshotName = display;
            }
        }

        var result = new List<RecipeGroup>(groups.Count);
        foreach (var group in groups.Values)
        {
            if (group.RecipeId is { } recipeId && recipeById.TryGetValue(recipeId, out var recipe))
            {
                group.Name = recipe.Name;
                group.Status = EnumTextConverters.RecipeStatusToText(recipe.Status);
            }
            else
            {
                // A hard-deleted recipe (M12) survives only as its stored
                // snapshot name (docs/M11_PLAN.md §1.5).
                group.Name = group.SnapshotName ?? "Unknown meal";
                group.Status = DeletedStatus;
            }

            result.Add(group);
        }

        return result;
    }

    private async Task<ProteinDistributionRecord> BuildProteinDistributionAsync(
        IReadOnlyList<EatenOccurrence> eaten,
        CancellationToken cancellationToken)
    {
        var eatenRecipeIds = eaten
            .Where(occurrence => occurrence.RecipeId is not null)
            .Select(occurrence => occurrence.RecipeId!.Value)
            .Distinct()
            .ToArray();

        var proteinRows = new List<ProteinRow>();
        if (eatenRecipeIds.Length > 0)
        {
            proteinRows.AddRange(await _dbContext.RecipeProteins
                .AsNoTracking()
                .Where(link => eatenRecipeIds.Contains(link.RecipeId))
                .Join(
                    _dbContext.Proteins.AsNoTracking(),
                    link => link.ProteinId,
                    protein => protein.Id,
                    (link, protein) => new ProteinRow(link.RecipeId, protein.Id, protein.Name))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        var proteinsByRecipe = proteinRows
            .GroupBy(row => row.RecipeId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var counts = new Dictionary<long, (string Name, int Count)>();
        var noPrimaryProtein = 0;
        foreach (var occurrence in eaten)
        {
            if (occurrence.RecipeId is { } recipeId
                && proteinsByRecipe.TryGetValue(recipeId, out var proteins)
                && proteins.Length > 0)
            {
                foreach (var protein in proteins)
                {
                    counts[protein.ProteinId] = counts.TryGetValue(protein.ProteinId, out var existing)
                        ? (existing.Name, existing.Count + 1)
                        : (protein.Name, 1);
                }
            }
            else
            {
                // No live recipe (tombstone) or a recipe with no protein rows is
                // reported explicitly, never silently dropped (docs/M11_PLAN.md §1.12).
                noPrimaryProtein++;
            }
        }

        var ordered = counts
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Value.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key)
            .Select(pair => new ProteinCountRecord(pair.Key, pair.Value.Name, pair.Value.Count))
            .ToArray();

        return new ProteinDistributionRecord(ordered, noPrimaryProtein);
    }

    private static string MonthLabel(DateOnly date) =>
        string.Concat(MonthNames[date.Month - 1], " ", YearLabel(date));

    private static string YearLabel(DateOnly date) =>
        date.Year.ToString(CultureInfo.InvariantCulture);

    /// <summary>A projected eaten occurrence used only inside this service.</summary>
    private sealed record EatenOccurrence(long? RecipeId, string? SnapshotRecipeName, DateOnly MealDate);

    /// <summary>A live recipe's ranking identity.</summary>
    private sealed record RecipeSummary(long Id, string Name, RecipeStatus Status);

    /// <summary>One recipe → protein join row.</summary>
    private sealed record ProteinRow(long RecipeId, long ProteinId, string Name);

    /// <summary>Mutable accumulator for one grouped recipe ranking row.</summary>
    private sealed class RecipeGroup(long? recipeId)
    {
        public long? RecipeId { get; } = recipeId;

        public string Name { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int TimesEaten { get; set; }

        public DateOnly LastEaten { get; set; }

        public DateOnly SnapshotDate { get; set; }

        public string? SnapshotName { get; set; }

        public RankedRecipeRecord ToRecord() =>
            new(RecipeId, Name, Status, TimesEaten, LastEaten);
    }
}
