using System.Globalization;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>The applied household-local history range.</summary>
public sealed record MealHistoryRangeRecord(DateOnly Start, DateOnly End, string Timezone);

/// <summary>One history page: the month key, its label, and its calendar bounds.</summary>
public sealed record MealHistoryMonthRecord(string Key, string Label, DateOnly Start, DateOnly End);

/// <summary>
/// One day of the meal log. Because
/// <c>calendar_entries.meal_date</c> is unique, a day row <b>is</b> the entry
/// <see cref="RecipeId"/> is null for a tombstone, <see cref="Name"/> is the
/// stored snapshot name for a tombstone and the recipe name (or the entry's own
/// display text for a non-recipe entry) otherwise, and <see cref="Protein"/> is
/// the entry's primary protein name, or null when the entry has no recipe or
/// its recipe lists no protein.
/// </summary>
public sealed record MealHistoryDayRecord(
    DateOnly Date,
    CalendarEntryType EntryType,
    long? RecipeId,
    string Name,
    string? Protein);

/// <summary>
/// The month cursors, one month per step inside the applied range
/// <see cref="Newer"/> is the next later month and
/// <see cref="Older"/> the next earlier one; both are null at the range ends.
/// </summary>
public sealed record MealHistoryPagingRecord(string? Newer, string? Older);

/// <summary>One month-grouped, strictly-past, entry-only page of the meal log.</summary>
public sealed record MealHistoryRecord(
    MealHistoryRangeRecord Range,
    MealHistoryMonthRecord Month,
    IReadOnlyList<MealHistoryDayRecord> Days,
    MealHistoryPagingRecord Paging);

/// <summary>The raw history query values, straight from the query string.</summary>
public sealed record MealHistoryRequest(
    string? Start,
    string? End,
    string? Month,
    string? EntryType,
    string? Recipe,
    string? Protein);

/// <summary>A history read: one page, or the invalid fields.</summary>
public abstract record MealHistoryOutcome
{
    private MealHistoryOutcome()
    {
    }

    public sealed record Success(MealHistoryRecord History) : MealHistoryOutcome;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
        : MealHistoryOutcome;
}

/// <summary>
/// The bounded, month-grouped, strictly-past meal-history read
/// It is a pure read over
/// the current <c>calendar_entries</c> rows: past dates are immutable in normal
/// use, so the current state is what was eaten (the change log is M12's
/// concern).
/// </summary>
/// <remarks>
/// <para>
/// <b>Bound.</b> The range defaults to the household calendar month plus the
/// eleven before it and is capped at <see cref="HouseholdClock.MaxRangeDays"/>
/// days; a malformed date, a reversed range, or a range over the cap is a
/// <c>400 VALIDATION_FAILED</c>.
/// </para>
/// <para>
/// <b>Page.</b> One month per request. <c>month</c> selects the page; by default
/// it is the newest month in the range that holds at least one entry, falling
/// back to the newest month in the range. Per the
/// presence check is over the bounded, strictly-past entries and is not narrowed
/// by the filters, so a filter may return an empty page and the caller pages
/// with the cursors.
/// </para>
/// <para>
/// <b>Days.</b> Only strictly past days (<c>meal_date &lt; household today</c>
/// the "eaten" rule) that have an entry — never blank filler — newest first
/// clamped to the applied range.
/// </para>
/// <para>
/// <b>Queries.</b> No N+1: at most three bounded reads per request, whichever
/// page is selected (an optional newest-entry lookup, the month's entries with
/// their live recipe name joined, and the month's recipe proteins).
/// </para>
/// </remarks>
public sealed class MealHistoryService(AppDbContext dbContext, HouseholdClock clock)
{
    /// <summary>
    /// Calendar months in the default range: the household's current month plus
    /// the eleven before it.
    /// </summary>
    public const int DefaultRangeMonths = 12;

    private const string UnknownMealName = "Unknown meal";

    private readonly AppDbContext _dbContext = dbContext;
    private readonly HouseholdClock _clock = clock;

    /// <summary>
    /// Reads one month of the meal log inside the bounded range, applying the
    /// optional server-side filters.
    /// </summary>
    public async Task<MealHistoryOutcome> GetHistoryAsync(
        MealHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var today = context.Today;

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var end = ReadDate(request.End, "end", errors) ?? today;
        var start = ReadDate(request.Start, "start", errors) ?? DefaultStart(end);
        var entryTypeFilter = ReadEntryType(request.EntryType, errors);
        var proteinFilter = ReadProteinId(request.Protein, errors);
        var (recipeIdFilter, recipeNameFilter) = ReadRecipe(request.Recipe, errors);
        if (errors.Count > 0)
        {
            return new MealHistoryOutcome.ValidationFailed(errors);
        }

        if (start > end)
        {
            return new MealHistoryOutcome.ValidationFailed(FieldError(
                "end",
                "end must be on or after start."));
        }

        if (end.DayNumber - start.DayNumber + 1 > HouseholdClock.MaxRangeDays)
        {
            return new MealHistoryOutcome.ValidationFailed(FieldError(
                "range",
                $"The history range may span at most {HouseholdClock.MaxRangeDays} days."));
        }

        var firstMonth = FirstOfMonth(start);
        var lastMonth = FirstOfMonth(end);
        DateOnly monthStart;
        if (request.Month is { } rawMonth)
        {
            var requestedMonth = ReadMonth(rawMonth, errors);
            if (errors.Count > 0)
            {
                return new MealHistoryOutcome.ValidationFailed(errors);
            }

            monthStart = requestedMonth!.Value;
            if (monthStart < firstMonth || monthStart > lastMonth)
            {
                return new MealHistoryOutcome.ValidationFailed(FieldError(
                    "month",
                    $"month must fall within the applied range ({HouseholdDateLabels.MonthKey(firstMonth)}"
                    + $"..{HouseholdDateLabels.MonthKey(lastMonth)})."));
            }
        }
        else
        {
            monthStart = await ResolveDefaultMonthAsync(start, end, today, lastMonth, cancellationToken)
                .ConfigureAwait(false);
        }

        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        var queryStart = monthStart > start ? monthStart : start;
        var queryEnd = monthEnd < end ? monthEnd : end;

        // The month's entries in one read, with each live recipe's current name
        // joined in. The past-only rule is the shared "eaten" rule.
        var rows = await _dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.MealDate >= queryStart
                && entry.MealDate <= queryEnd
                && entry.MealDate < today)
            .OrderByDescending(entry => entry.MealDate)
            .Select(entry => new HistoryEntryRow(
                entry.MealDate,
                entry.EntryType,
                entry.RecipeId,
                entry.IncludeGroceries,
                entry.SnapshotRecipeName,
                entry.SnapshotDisplayText,
                entry.Recipe == null ? null : entry.Recipe.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var primaryProteins = await LoadPrimaryProteinsAsync(rows, cancellationToken).ConfigureAwait(false);

        var days = new List<MealHistoryDayRecord>(rows.Count);
        foreach (var row in rows)
        {
            ProteinRow? primaryProtein = null;
            if (row.IncludeGroceries && row.RecipeId is { } recipeId)
            {
                primaryProteins.TryGetValue(recipeId, out primaryProtein);
            }

            if (entryTypeFilter is { } entryType && row.EntryType != entryType)
            {
                continue;
            }

            if (recipeIdFilter is { } wantedRecipeId && row.RecipeId != wantedRecipeId)
            {
                continue;
            }

            if (recipeNameFilter is { } wantedName
                && !string.Equals(
                    MealOccurrenceRules.NormalizeSnapshotName(row.SnapshotRecipeName),
                    wantedName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            // The primary protein filter matches the same deterministic choice
            // the row reports, so the filter and the displayed protein agree.
            if (proteinFilter is { } wantedProtein && primaryProtein?.ProteinId != wantedProtein)
            {
                continue;
            }

            days.Add(new MealHistoryDayRecord(
                row.MealDate,
                row.EntryType,
                row.RecipeId,
                ResolveName(row),
                primaryProtein?.Name));
        }

        days.Sort(static (left, right) => right.Date.CompareTo(left.Date));

        return new MealHistoryOutcome.Success(new MealHistoryRecord(
            new MealHistoryRangeRecord(start, end, context.TimeZone.Id),
            new MealHistoryMonthRecord(
                HouseholdDateLabels.MonthKey(monthStart),
                HouseholdDateLabels.MonthLabel(monthStart),
                monthStart,
                monthEnd),
            days,
            new MealHistoryPagingRecord(
                monthStart < lastMonth ? HouseholdDateLabels.MonthKey(monthStart.AddMonths(1)) : null,
                monthStart > firstMonth ? HouseholdDateLabels.MonthKey(monthStart.AddMonths(-1)) : null)));
    }

    /// <summary>
    /// The default page: the newest month in the range with at least one
    /// strictly-past entry, falling back to the newest month in the range when
    /// no month has one.
    /// </summary>
    private async Task<DateOnly> ResolveDefaultMonthAsync(
        DateOnly start,
        DateOnly end,
        DateOnly today,
        DateOnly lastMonth,
        CancellationToken cancellationToken)
    {
        var newest = await _dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.MealDate >= start && entry.MealDate <= end && entry.MealDate < today)
            .OrderByDescending(entry => entry.MealDate)
            .Select(entry => (DateOnly?)entry.MealDate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return newest is { } date ? FirstOfMonth(date) : lastMonth;
    }

    /// <summary>
    /// Each involved recipe's primary protein: the lowest protein id, chosen
    /// deterministically because a recipe may list several proteins
    /// </summary>
    private async Task<Dictionary<long, ProteinRow>> LoadPrimaryProteinsAsync(
        IReadOnlyList<HistoryEntryRow> rows,
        CancellationToken cancellationToken)
    {
        var recipeIds = rows
            .Where(row => row.IncludeGroceries && row.RecipeId is not null)
            .Select(row => row.RecipeId!.Value)
            .Distinct()
            .ToArray();
        if (recipeIds.Length == 0)
        {
            return [];
        }

        var proteins = await _dbContext.RecipeProteins
            .AsNoTracking()
            .Where(link => recipeIds.Contains(link.RecipeId))
            .Join(
                _dbContext.Proteins.AsNoTracking(),
                link => link.ProteinId,
                protein => protein.Id,
                (link, protein) => new { link.RecipeId, ProteinId = protein.Id, protein.Name })
            .OrderBy(row => row.RecipeId)
            .ThenBy(row => row.ProteinId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var primaryProteins = new Dictionary<long, ProteinRow>();
        foreach (var protein in proteins)
        {
            primaryProteins.TryAdd(
                protein.RecipeId,
                new ProteinRow(protein.RecipeId, protein.ProteinId, protein.Name));
        }

        return primaryProteins;
    }

    /// <summary>
    /// The row's display name: the live recipe name
    /// when the recipe survives, else the stored snapshot name (a tombstone, or
    /// a recipe M12 hard-deleted), else the entry's own display text (a
    /// restaurant, host, or custom label).
    /// </summary>
    private static string ResolveName(HistoryEntryRow row)
    {
        if (row.LiveRecipeName is { } liveName)
        {
            return liveName;
        }

        if (MealOccurrenceRules.SnapshotDisplayName(row.SnapshotRecipeName) is { } snapshot)
        {
            return snapshot;
        }

        var display = row.SnapshotDisplayText.Trim();
        return display.Length > 0 ? display : UnknownMealName;
    }

    private static DateOnly? ReadDate(
        string? raw,
        string field,
        Dictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(
            raw,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date))
        {
            return date;
        }

        errors[field] = [$"{field} must be a YYYY-MM-DD household-local date."];
        return null;
    }

    private static DateOnly? ReadMonth(string raw, Dictionary<string, string[]> errors)
    {
        if (raw.Length == "yyyy-MM".Length
            && DateOnly.TryParseExact(
                string.Concat(raw, "-01"),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var month))
        {
            return month;
        }

        errors["month"] = ["month must be a YYYY-MM household-local month."];
        return null;
    }

    private static CalendarEntryType? ReadEntryType(
        string? raw,
        Dictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        if (EnumTextConverters.TryCalendarEntryTypeFromText(raw, out var entryType))
        {
            return entryType;
        }

        errors["entry_type"] = ["entry_type must be one of the eight calendar entry types."];
        return null;
    }

    private static long? ReadProteinId(string? raw, Dictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var proteinId)
            && proteinId > 0)
        {
            return proteinId;
        }

        errors["protein"] = ["protein must be a positive protein id."];
        return null;
    }

    /// <summary>
    /// The recipe filter accepts a live recipe id or a stored snapshot name, so
    /// a hard-deleted recipe's meals stay filterable.
    /// </summary>
    private static (long? RecipeId, string? SnapshotName) ReadRecipe(
        string? raw,
        Dictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return (null, null);
        }

        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var recipeId))
        {
            if (recipeId > 0)
            {
                return (recipeId, null);
            }

            errors["recipe"] = ["recipe must be a positive recipe id or a recipe name."];
            return (null, null);
        }

        var normalized = MealOccurrenceRules.NormalizeSnapshotName(raw);
        if (normalized is not null)
        {
            return (null, normalized);
        }

        errors["recipe"] = ["recipe must be a positive recipe id or a recipe name."];
        return (null, null);
    }

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private static DateOnly DefaultStart(DateOnly end) =>
        FirstOfMonth(end).AddMonths(-(DefaultRangeMonths - 1));

    private static IReadOnlyDictionary<string, string[]> FieldError(string field, string message) =>
        new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] };

    /// <summary>One projected calendar entry of the month page.</summary>
    private sealed record HistoryEntryRow(
        DateOnly MealDate,
        CalendarEntryType EntryType,
        long? RecipeId,
        bool IncludeGroceries,
        string? SnapshotRecipeName,
        string SnapshotDisplayText,
        string? LiveRecipeName);

    /// <summary>One recipe → protein join row.</summary>
    private sealed record ProteinRow(long RecipeId, long ProteinId, string Name);
}
