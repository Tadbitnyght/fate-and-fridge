using System.Globalization;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The raw grocery range query. Each member is the
/// literal query-string value (or null when absent), so the service can tell
/// "absent" from "malformed" and enforce the preset xor start/days rules.
/// </summary>
public sealed record GroceryRangeRequest(string? Preset, string? Start, string? Days);

/// <summary>One resolved, concrete grocery range.</summary>
public sealed record GroceryRangeResolution(string Preset, DateOnly Start, DateOnly End, int DayCount);

/// <summary>
/// One projected grocery line. <see cref="Identity"/> is
/// the <see cref="GroceryItemIdentity"/> value shared with the check API; recipe
/// lines reuse the aggregation identity while manual lines hash their own id.
/// The department is null when the line belongs to the trailing "Other" group.
/// </summary>
public sealed record GroceryListItemRecord(
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
    IReadOnlyList<GroceryItemSource> Sources);

/// <summary>One department bucket of ordered grocery lines.</summary>
public sealed record GroceryDepartmentRecord(
    long? DepartmentId,
    string Name,
    int SortOrder,
    bool IsOther,
    IReadOnlyList<GroceryListItemRecord> Items);

/// <summary>The full grocery projection for a resolved range.</summary>
public sealed record GroceryListRecord(
    GroceryRangeResolution Range,
    IReadOnlyList<GroceryDepartmentRecord> Departments);

/// <summary>Outcome of a grocery list read.</summary>
public abstract record GroceryListOutcome
{
    private GroceryListOutcome()
    {
    }

    public sealed record Success(GroceryListRecord List) : GroceryListOutcome;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
        : GroceryListOutcome;

    public sealed record RangeTooLarge : GroceryListOutcome;
}

/// <summary>
/// Builds the live grocery projection. It
/// resolves the requested range (preset or explicit start/days, falling back to
/// the caller's remembered range and then Next 7 Days), gathers the
/// <c>include_groceries</c> calendar entries in the range, projects their recipe
/// ingredients and the range-matching manual items, delegates the math to
/// <see cref="GroceryAggregationService"/>, attaches the household-wide check
/// state, and orders departments and lines. No list is persisted: it is a live
/// projection over calendar, recipe, manual, and check-state rows.
/// </summary>
public sealed class GroceryListService(
    AppDbContext dbContext,
    HouseholdClock clock,
    GroceryAggregationService aggregation,
    GroceryPreferenceService preferences)
{
    /// <summary>Next 3 Days preset value.</summary>
    public const string PresetNext3Days = "next_3_days";

    /// <summary>Next 7 Days preset value (the first-open default).</summary>
    public const string PresetNext7Days = "next_7_days";

    /// <summary>Next 14 Days preset value.</summary>
    public const string PresetNext14Days = "next_14_days";

    /// <summary>Rest of Month preset value.</summary>
    public const string PresetRestOfMonth = "rest_of_month";

    /// <summary>Explicit start + day count preset value.</summary>
    public const string PresetCustom = "custom";

    /// <summary>The server-side cap on a resolved grocery range, in days.</summary>
    public const int MaxRangeDays = 366;

    private const string OtherDepartmentName = "Other";

    private const string RecipeSourceType = "recipe";

    private const string ManualSourceType = "manual";

    private static readonly string[] StandardPresets =
    [
        PresetNext3Days,
        PresetNext7Days,
        PresetNext14Days,
        PresetRestOfMonth,
    ];

    /// <summary>
    /// Resolves the range and builds the ordered projection for the actor. When
    /// the query supplies nothing, the actor's remembered range is used, falling
    /// back to Next 7 Days; reads never write the preference (owns that).
    /// </summary>
    public async Task<GroceryListOutcome> GetListAsync(
        GroceryRangeRequest request,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);

        var rangeOutcome = await ResolveRangeAsync(request, actorUserId, context.Today, cancellationToken)
            .ConfigureAwait(false);
        switch (rangeOutcome)
        {
            case RangeResolutionOutcome.ValidationFailed failed:
                return new GroceryListOutcome.ValidationFailed(failed.FieldErrors);
            case RangeResolutionOutcome.RangeTooLarge:
                return new GroceryListOutcome.RangeTooLarge();
        }

        var range = ((RangeResolutionOutcome.Success)rangeOutcome).Resolution;
        return await BuildListAsync(range, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GroceryListOutcome> BuildListAsync(
        GroceryRangeResolution range,
        CancellationToken cancellationToken)
    {
        // Only include_groceries entries carrying a recipe contribute
        // eat_out/fend/go_to/vacation never do.
        var entries = await dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry =>
                entry.MealDate >= range.Start
                && entry.MealDate <= range.End
                && entry.IncludeGroceries
                && entry.RecipeId != null)
            .Select(entry => new ContributingEntry(
                entry.RecipeId!.Value,
                entry.MealDate,
                entry.SnapshotRecipeName,
                entry.SnapshotDisplayText))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recipeIds = entries.Select(entry => entry.RecipeId).Distinct().ToArray();

        var recipeNames = new Dictionary<long, string>();
        var recipeIngredients = new List<RecipeIngredientInfo>();
        if (recipeIds.Length > 0)
        {
            foreach (var recipe in await dbContext.Recipes
                .AsNoTracking()
                .Where(candidate => recipeIds.Contains(candidate.Id))
                .Select(candidate => new { candidate.Id, candidate.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                recipeNames[recipe.Id] = recipe.Name;
            }

            recipeIngredients.AddRange(await dbContext.RecipeIngredients
                .AsNoTracking()
                .Where(ingredient => recipeIds.Contains(ingredient.RecipeId))
                .Select(ingredient => new RecipeIngredientInfo(
                    ingredient.RecipeId,
                    ingredient.IngredientId,
                    ingredient.QuantityNumeric,
                    ingredient.UnitId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
        }

        // Manual items show only for the exact active range: the stored
        // (range_start, range_days) must equal the resolved range, so a manual
        // item is hidden the moment the range changes.
        var manualItems = await dbContext.ManualGroceryItems
            .AsNoTracking()
            .Where(item => item.RangeStart == range.Start && item.RangeDays == range.DayCount)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ingredientMap = await LoadIngredientMapAsync(
            recipeIngredients
                .Select(ingredient => ingredient.IngredientId)
                .Concat(manualItems
                    .Where(item => item.IngredientId is not null)
                    .Select(item => item.IngredientId!.Value))
                .Distinct()
                .ToArray(),
            cancellationToken).ConfigureAwait(false);

        var units = await LoadUnitsAsync(
            recipeIngredients
                .Where(ingredient => ingredient.UnitId is not null)
                .Select(ingredient => ingredient.UnitId!.Value)
                .Concat(manualItems
                    .Where(item => item.UnitId is not null)
                    .Select(item => item.UnitId!.Value))
                .Distinct()
                .ToArray(),
            cancellationToken).ConfigureAwait(false);

        var items = BuildRecipeItems(entries, recipeNames, recipeIngredients, ingredientMap, units);
        items.AddRange(BuildManualItems(manualItems, ingredientMap, units));

        await AttachCheckStateAsync(items, cancellationToken).ConfigureAwait(false);

        var departments = await LoadDepartmentsAsync(cancellationToken).ConfigureAwait(false);

        return new GroceryListOutcome.Success(
            new GroceryListRecord(range, OrderDepartments(items, departments)));
    }

    private List<GroceryListItemRecord> BuildRecipeItems(
        IReadOnlyList<ContributingEntry> entries,
        IReadOnlyDictionary<long, string> recipeNames,
        IReadOnlyList<RecipeIngredientInfo> recipeIngredients,
        IReadOnlyDictionary<long, IngredientInfo> ingredientMap,
        IReadOnlyDictionary<long, ConversionUnit> units)
    {
        var byRecipe = recipeIngredients
            .GroupBy(ingredient => ingredient.RecipeId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var inputs = new List<GroceryIngredientInput>();
        foreach (var entry in entries)
        {
            if (!byRecipe.TryGetValue(entry.RecipeId, out var ingredients))
            {
                continue;
            }

            // The recipe may have been retired or removed; its stored snapshot
            // still attributes the line, and a missing recipe never throws.
            var recipeName = recipeNames.TryGetValue(entry.RecipeId, out var name)
                ? name
                : entry.SnapshotRecipeName ?? entry.SnapshotDisplayText;

            foreach (var ingredient in ingredients)
            {
                var canonicalId = ResolveCanonicalIngredient(ingredient.IngredientId, ingredientMap);
                ingredientMap.TryGetValue(canonicalId, out var canonical);
                inputs.Add(new GroceryIngredientInput(
                    canonicalId,
                    canonical?.CanonicalName ?? string.Empty,
                    ingredient.QuantityNumeric,
                    ingredient.UnitId is { } unitId && units.TryGetValue(unitId, out var unit)
                        ? unit
                        : null,
                    entry.RecipeId,
                    recipeName,
                    entry.MealDate));
            }
        }

        var aggregated = aggregation.Aggregate(inputs);
        var items = new List<GroceryListItemRecord>(aggregated.Count);
        foreach (var item in aggregated)
        {
            items.Add(new GroceryListItemRecord(
                item.IdentityHash,
                item.Name,
                item.QuantityText,
                item.Quantity,
                item.UnitSymbol,
                item.IngredientId,
                LookupDepartment(item.IngredientId, ingredientMap),
                Checked: false,
                RecipeSourceType,
                ManualItemId: null,
                item.Sources));
        }

        return items;
    }

    private static List<GroceryListItemRecord> BuildManualItems(
        IReadOnlyList<ManualGroceryItem> manualItems,
        IReadOnlyDictionary<long, IngredientInfo> ingredientMap,
        IReadOnlyDictionary<long, ConversionUnit> units)
    {
        var items = new List<GroceryListItemRecord>(manualItems.Count);
        foreach (var manual in manualItems)
        {
            var canonicalId = manual.IngredientId is { } ingredientId
                ? ResolveCanonicalIngredient(ingredientId, ingredientMap)
                : (long?)null;

            // Department defaults to the linked (canonical) ingredient, else the
            // line falls to the trailing "Other" group.
            var departmentId = manual.DepartmentId
                ?? (canonicalId is { } canonical ? LookupDepartment(canonical, ingredientMap) : null);

            var quantity = manual.QuantityNumeric;
            var unitSymbol = quantity is not null
                && manual.UnitId is { } unitId
                && units.TryGetValue(unitId, out var unit)
                    ? unit.Symbol
                    : null;

            items.Add(new GroceryListItemRecord(
                GroceryItemIdentity.ForManualItem(manual.Id),
                manual.Name,
                GroceryAggregationService.FormatQuantityText(quantity),
                quantity,
                unitSymbol,
                canonicalId,
                departmentId,
                Checked: false,
                ManualSourceType,
                ManualItemId: manual.Id,
                Sources: []));
        }

        return items;
    }

    private async Task AttachCheckStateAsync(
        List<GroceryListItemRecord> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var identities = items
            .Select(item => item.Identity)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var checkedIdentities = await dbContext.GroceryCheckStates
            .AsNoTracking()
            .Where(state => state.Checked && identities.Contains(state.ItemIdentityHash))
            .Select(state => state.ItemIdentityHash)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (checkedIdentities.Count == 0)
        {
            return;
        }

        var checkedSet = checkedIdentities.ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            if (checkedSet.Contains(items[index].Identity))
            {
                items[index] = items[index] with { Checked = true };
            }
        }
    }

    private static IReadOnlyList<GroceryDepartmentRecord> OrderDepartments(
        IReadOnlyList<GroceryListItemRecord> items,
        IReadOnlyDictionary<long, DepartmentInfo> departments)
    {
        var normalized = items
            .Select(item => item.DepartmentId is { } id && departments.ContainsKey(id)
                ? item
                : item with { DepartmentId = null })
            .ToList();

        var groups = new List<GroceryDepartmentRecord>();
        foreach (var group in normalized.GroupBy(item => item.DepartmentId))
        {
            if (group.Key is { } departmentId && departments.TryGetValue(departmentId, out var department))
            {
                groups.Add(new GroceryDepartmentRecord(
                    departmentId,
                    department.Name,
                    department.SortOrder,
                    IsOther: false,
                    SortItems(group)));
                continue;
            }

            groups.Add(new GroceryDepartmentRecord(
                DepartmentId: null,
                OtherDepartmentName,
                int.MaxValue,
                IsOther: true,
                SortItems(group)));
        }

        // Departments follow sort_order then name; the null-department "Other"
        // group carries int.MaxValue so it always sorts last.
        return groups
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<GroceryListItemRecord> SortItems(
        IEnumerable<GroceryListItemRecord> items) =>
        items
            .OrderBy(item => item.Checked)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Identity, StringComparer.Ordinal)
            .ThenBy(item => item.Unit ?? string.Empty, StringComparer.Ordinal)
            .ToList();

    private async Task<Dictionary<long, IngredientInfo>> LoadIngredientMapAsync(
        IReadOnlyCollection<long> ingredientIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, IngredientInfo>();
        var pending = new HashSet<long>(ingredientIds);
        while (pending.Count > 0)
        {
            var batch = pending.ToArray();
            pending.Clear();

            var rows = await dbContext.Ingredients
                .AsNoTracking()
                .Where(ingredient => batch.Contains(ingredient.Id))
                .Select(ingredient => new IngredientInfo(
                    ingredient.Id,
                    ingredient.CanonicalName,
                    ingredient.DepartmentId,
                    ingredient.MergedIntoIngredientId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (rows.Count == 0)
            {
                break;
            }

            foreach (var row in rows)
            {
                map[row.Id] = row;
                if (row.MergedIntoIngredientId is { } target && !map.ContainsKey(target))
                {
                    pending.Add(target);
                }
            }
        }

        return map;
    }

    private async Task<Dictionary<long, ConversionUnit>> LoadUnitsAsync(
        IReadOnlyCollection<long> unitIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, ConversionUnit>();
        if (unitIds.Count == 0)
        {
            return map;
        }

        var batch = unitIds.ToArray();
        var rows = await dbContext.Units
            .AsNoTracking()
            .Where(unit => batch.Contains(unit.Id))
            .Select(unit => new ConversionUnit(
                unit.Id,
                unit.Symbol,
                unit.Dimension,
                unit.BaseFactor))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            map[row.Id!.Value] = row;
        }

        return map;
    }

    private async Task<Dictionary<long, DepartmentInfo>> LoadDepartmentsAsync(
        CancellationToken cancellationToken) =>
        (await dbContext.GroceryDepartments
            .AsNoTracking()
            .Select(department => new DepartmentInfo(
                department.Id,
                department.Name,
                department.SortOrder))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
        .ToDictionary(department => department.Id);

    private static long ResolveCanonicalIngredient(
        long ingredientId,
        IReadOnlyDictionary<long, IngredientInfo> ingredientMap)
    {
        var current = ingredientId;
        var visited = new HashSet<long>();
        while (ingredientMap.TryGetValue(current, out var info)
            && info.MergedIntoIngredientId is { } target
            && target != current
            && visited.Add(current))
        {
            current = target;
        }

        return current;
    }

    private static long? LookupDepartment(
        long? ingredientId,
        IReadOnlyDictionary<long, IngredientInfo> ingredientMap) =>
        ingredientId is { } id && ingredientMap.TryGetValue(id, out var info)
            ? info.DepartmentId
            : null;

    private async Task<RangeResolutionOutcome> ResolveRangeAsync(
        GroceryRangeRequest request,
        long? actorUserId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var preset = Normalize(request.Preset);
        var startProvided = request.Start is not null;
        var daysProvided = request.Days is not null;

        if (preset is not null)
        {
            if (string.Equals(preset, PresetCustom, StringComparison.Ordinal))
            {
                return ResolveExplicitCustom(request, today);
            }

            if (!StandardPresets.Contains(preset, StringComparer.Ordinal))
            {
                return new RangeResolutionOutcome.ValidationFailed(FieldError(
                    "preset",
                    $"preset must be one of {string.Join(", ", [.. StandardPresets, PresetCustom])}."));
            }

            if (startProvided || daysProvided)
            {
                return new RangeResolutionOutcome.ValidationFailed(FieldError(
                    "preset",
                    "preset cannot be combined with start or days; use preset=custom for an explicit range."));
            }

            return ResolveStandardPreset(preset, today);
        }

        if (startProvided || daysProvided)
        {
            return ResolveExplicitCustom(request, today);
        }

        // The remembered range comes from the caller's preference; the
        // single-user system actor's row lives in application_settings, so this
        // read is not multiuser-only. Reads never write the preference.
        var preference = await preferences.GetAsync(actorUserId, cancellationToken).ConfigureAwait(false);
        if (TryResolvePreference(preference, today, out var preferred))
        {
            return new RangeResolutionOutcome.Success(preferred);
        }

        return ResolveStandardPreset(PresetNext7Days, today);
    }

    private static RangeResolutionOutcome ResolveExplicitCustom(
        GroceryRangeRequest request,
        DateOnly today)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var start = ReadStart(request.Start, errors);
        var days = ReadDays(request.Days, errors);
        if (errors.Count > 0)
        {
            return new RangeResolutionOutcome.ValidationFailed(errors);
        }

        return ResolveCustom(start!.Value, days!.Value, PresetCustom, today);
    }

    private static RangeResolutionOutcome ResolveCustom(
        DateOnly start,
        int days,
        string preset,
        DateOnly today)
    {
        if (start < today)
        {
            return new RangeResolutionOutcome.ValidationFailed(
                FieldError("start", "start must be today or later."));
        }

        if (days > MaxRangeDays)
        {
            return new RangeResolutionOutcome.RangeTooLarge();
        }

        DateOnly end;
        try
        {
            end = start.AddDays(days - 1);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new RangeResolutionOutcome.RangeTooLarge();
        }

        return new RangeResolutionOutcome.Success(new GroceryRangeResolution(preset, start, end, days));
    }

    private static RangeResolutionOutcome ResolveStandardPreset(string preset, DateOnly today)
    {
        var end = preset switch
        {
            PresetNext3Days => today.AddDays(2),
            PresetNext7Days => today.AddDays(6),
            PresetNext14Days => today.AddDays(13),
            PresetRestOfMonth => LastDayOfMonth(today),
            _ => today,
        };

        return new RangeResolutionOutcome.Success(new GroceryRangeResolution(
            preset,
            today,
            end,
            end.DayNumber - today.DayNumber + 1));
    }

    private static bool TryResolvePreference(
        GroceryPreferenceSnapshot preference,
        DateOnly today,
        out GroceryRangeResolution resolution)
    {
        resolution = null!;

        var preset = Normalize(preference.RangePreset);
        if (preset is null)
        {
            return false;
        }

        if (string.Equals(preset, PresetCustom, StringComparison.Ordinal))
        {
            if (preference.RangeStart is not { } start
                || preference.RangeDays is not { } days
                || start < today
                || days <= 0
                || days > MaxRangeDays)
            {
                return false;
            }

            DateOnly end;
            try
            {
                end = start.AddDays(days - 1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            resolution = new GroceryRangeResolution(PresetCustom, start, end, days);
            return true;
        }

        if (!StandardPresets.Contains(preset, StringComparer.Ordinal))
        {
            return false;
        }

        resolution = ((RangeResolutionOutcome.Success)ResolveStandardPreset(preset, today)).Resolution;
        return true;
    }

    private static DateOnly? ReadStart(string? raw, Dictionary<string, string[]> errors)
    {
        if (raw is null
            || !DateOnly.TryParseExact(
                raw,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            errors["start"] = ["start is required and must be a YYYY-MM-DD date."];
            return null;
        }

        return date;
    }

    private static int? ReadDays(string? raw, Dictionary<string, string[]> errors)
    {
        if (raw is null
            || !int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days <= 0)
        {
            errors["days"] = ["days is required and must be a positive integer."];
            return null;
        }

        return days;
    }

    private static DateOnly LastDayOfMonth(DateOnly date) =>
        new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyDictionary<string, string[]> FieldError(string field, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] };

    private abstract record RangeResolutionOutcome
    {
        private RangeResolutionOutcome()
        {
        }

        public sealed record Success(GroceryRangeResolution Resolution) : RangeResolutionOutcome;

        public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
            : RangeResolutionOutcome;

        public sealed record RangeTooLarge : RangeResolutionOutcome;
    }

    private sealed record ContributingEntry(
        long RecipeId,
        DateOnly MealDate,
        string? SnapshotRecipeName,
        string SnapshotDisplayText);

    private sealed record RecipeIngredientInfo(
        long RecipeId,
        long IngredientId,
        decimal? QuantityNumeric,
        long? UnitId);

    private sealed record IngredientInfo(
        long Id,
        string CanonicalName,
        long? DepartmentId,
        long? MergedIntoIngredientId);

    private sealed record DepartmentInfo(long Id, string Name, int SortOrder);
}
