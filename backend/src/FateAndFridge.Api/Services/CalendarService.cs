using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Imports;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>The curated special-event icon catalog (docs/M5_PLAN.md §1.14).</summary>
public static class CalendarIcons
{
    /// <summary>Stable icon keys rendered by the frontend inline-SVG set.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "turkey",
        "cake",
        "heart",
        "star",
        "pumpkin",
        "gift",
        "snowflake",
        "flag",
        "fireworks",
        "clover",
        "leaf",
        "egg",
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    /// <summary>True when the key is one of the curated icons.</summary>
    public static bool IsKnown(string? key) => key is not null && Known.Contains(key);
}

/// <summary>Minimal recipe summary embedded on a calendar entry.</summary>
public sealed record CalendarRecipeSummary(long Id, string Name, string? ImageUrl, string Status);

/// <summary>A calendar entry as returned by the API (docs/M5_PLAN.md §2.4).</summary>
public sealed record CalendarEntryRecord(
    long Id,
    string EntryType,
    CalendarRecipeSummary? Recipe,
    string DisplayText,
    bool IsLocked,
    int? DinerCount,
    int EffectiveDinerCount,
    string? RestaurantName,
    string? LocationText,
    string? CustomLabel,
    string? Notes,
    string? IconKey,
    bool IncludeGroceries,
    bool BlocksAutomation,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One exact-recipe occurrence inside a repeat window.</summary>
public sealed record CalendarOccurrence(DateOnly Date, string EntryType);

/// <summary>A non-blocking exact-recipe repeat warning (docs/M5_PLAN.md §1.13).</summary>
public sealed record CalendarRepeatWarning(
    long RecipeId,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    IReadOnlyList<CalendarOccurrence> Occurrences);

/// <summary>One calendar date with its optional entry and repeat warning.</summary>
public sealed record CalendarDayRecord(
    DateOnly Date,
    bool IsToday,
    bool IsHistorical,
    bool CanEdit,
    CalendarEntryRecord? Entry,
    CalendarRepeatWarning? RepeatWarning);

/// <summary>A bounded calendar range read.</summary>
public sealed record CalendarRangeRecord(
    DateOnly Start,
    DateOnly End,
    DateOnly Today,
    int WeekStartsOn,
    IReadOnlyList<CalendarDayRecord> Entries);

/// <summary>Outcome of a repeat check for a proposed recipe on a date.</summary>
public sealed record CalendarRepeatCheckRecord(
    bool IsRepeat,
    DateOnly WindowStart,
    DateOnly WindowEnd,
    IReadOnlyList<CalendarOccurrence> Occurrences);

/// <summary>Parsed full-replace calendar write body.</summary>
public sealed record CalendarWriteRequest(
    string? EntryType,
    long? RecipeId,
    int? DinerCount,
    string? RestaurantName,
    string? LocationText,
    string? CustomLabel,
    string? Notes,
    string? IconKey);

/// <summary>
/// The validated, sanitized fields of a calendar write (docs/M5_PLAN.md §1.6).
/// <see cref="CalendarService.ValidateWriteRequestAsync"/> produces it once, then
/// <see cref="CalendarService.ApplyFields"/> stores it, so the normal upsert and
/// the M12 historical correction share exactly one rule set.
/// </summary>
internal sealed record CalendarWriteFields(
    CalendarEntryType EntryType,
    Recipe? Recipe,
    int? DinerCount,
    string? RestaurantName,
    string? LocationText,
    string? CustomLabel,
    string? Notes,
    string? IconKey,
    string DisplayText,
    bool IncludeGroceries);

/// <summary>Outcome of a calendar mutation.</summary>
public abstract record CalendarMutationResult
{
    /// <summary>The write committed; the day reflects the stored state.</summary>
    public sealed record Success(CalendarDayRecord Day) : CalendarMutationResult;

    /// <summary>Field validation failed; <see cref="Code"/> opts into a specific code.</summary>
    public sealed record ValidationFailed(
        IReadOnlyDictionary<string, string[]> FieldErrors,
        string? Code = null) : CalendarMutationResult;

    /// <summary>A non-validation failure (past date, blank lock, missing entry).</summary>
    public sealed record Failure(int StatusCode, string Code, string Message) : CalendarMutationResult;
}

/// <summary>
/// M5 calendar core service (docs/M5_PLAN.md §2): reads with the household clock,
/// full-replace writes with per-type validation, snapshots (refresh future /
/// freeze past), lock/unlock, and the manual-selection repeat check. Every write
/// stages exactly one <see cref="CalendarEntryHistory"/> row and one M12 audit
/// row with the actor, the date target, and the before/after documents
/// (docs/M12_PLAN.md §1.3); the entry, its history, and the audit row commit
/// atomically.
/// </summary>
public sealed class CalendarService(
    AppDbContext dbContext,
    HouseholdClock clock,
    TimeProvider timeProvider,
    IAuditService auditService)
{
    public const int MinDinerCount = 1;

    public const int MaxDinerCount = 99;

    /// <summary>Exact-recipe window: 7 days back and 14 days forward, inclusive.</summary>
    public const int RepeatLookbackDays = 7;

    /// <summary>See <see cref="RepeatLookbackDays"/>.</summary>
    public const int RepeatLookaheadDays = 14;

    /// <summary>Special types that may carry an icon (docs/M5_PLAN.md §1.14).</summary>
    private static readonly CalendarEntryType[] IconTypes =
    [
        CalendarEntryType.Holiday,
        CalendarEntryType.Misc,
        CalendarEntryType.HostFamilyDinner,
        CalendarEntryType.GoToFamilyDinner,
    ];

    /// <summary>Reads a bounded range of non-blank days, ascending.</summary>
    public async Task<CalendarRangeRecord> GetRangeAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);

        var entries = await dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.MealDate >= start && entry.MealDate <= end)
            .OrderBy(entry => entry.MealDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var recipes = await LoadRecipeSummariesAsync(
            entries.Where(entry => entry.RecipeId is not null)
                .Select(entry => entry.RecipeId!.Value)
                .Distinct()
                .ToArray(),
            cancellationToken).ConfigureAwait(false);

        var days = new List<CalendarDayRecord>(entries.Count);
        foreach (var entry in entries)
        {
            recipes.TryGetValue(entry.RecipeId ?? 0, out var recipe);
            days.Add(ToDayRecord(entry.MealDate, context, entry, recipe, repeatWarning: null));
        }

        return new CalendarRangeRecord(start, end, context.Today, context.WeekStartsOn, days);
    }

    /// <summary>Reads one date, blank or not.</summary>
    public async Task<CalendarDayRecord> GetDayAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var entry = await dbContext.CalendarEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.MealDate == date, cancellationToken)
            .ConfigureAwait(false);

        CalendarRecipeSummary? recipe = null;
        CalendarRepeatWarning? warning = null;
        if (entry?.RecipeId is { } recipeId)
        {
            recipe = await LoadRecipeSummaryAsync(recipeId, cancellationToken).ConfigureAwait(false);
            warning = await ComputeRepeatWarningAsync(date, recipeId, cancellationToken).ConfigureAwait(false);
        }

        return ToDayRecord(date, context, entry, recipe, warning);
    }

    /// <summary>Exact-recipe repeat check for a proposed recipe on a date.</summary>
    public async Task<CalendarRepeatCheckRecord> RepeatCheckAsync(
        DateOnly date,
        long recipeId,
        CancellationToken cancellationToken = default)
    {
        var windowStart = date.AddDays(-RepeatLookbackDays);
        var windowEnd = date.AddDays(RepeatLookaheadDays);
        var occurrences = await LoadOccurrencesAsync(
            windowStart,
            windowEnd,
            recipeId,
            excludeDate: date,
            cancellationToken).ConfigureAwait(false);

        return new CalendarRepeatCheckRecord(
            occurrences.Count > 0,
            windowStart,
            windowEnd,
            occurrences);
    }

    /// <summary>Full-replace upsert of one date's entry.</summary>
    public async Task<CalendarMutationResult> UpsertAsync(
        DateOnly date,
        CalendarWriteRequest request,
        long? actorUserId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (HouseholdClock.IsHistorical(date, context))
        {
            return PastDate();
        }

        var (fields, errors) = await ValidateWriteRequestAsync(
                dbContext,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (fields is null)
        {
            return new CalendarMutationResult.ValidationFailed(errors, RequiredCode(errors));
        }

        var now = timeProvider.GetUtcNow();
        var existing = await dbContext.CalendarEntries
            .FirstOrDefaultAsync(candidate => candidate.MealDate == date, cancellationToken)
            .ConfigureAwait(false);

        string? beforeJson = null;
        CalendarEntry entry;
        if (existing is null)
        {
            entry = new CalendarEntry
            {
                MealDate = date,
                SnapshotDisplayText = fields.DisplayText,
                CreatedAt = now,
                CreatedByUserId = actorUserId,
            };
            dbContext.CalendarEntries.Add(entry);
        }
        else
        {
            entry = existing;
            beforeJson = SnapshotJson(entry);
        }

        ApplyFields(entry, fields, now, actorUserId);

        // Entry, history, and audit commit atomically (docs/M5_PLAN.md §4,
        // docs/M12_PLAN.md §2.1).
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var afterJson = SnapshotJson(entry);
        StageHistoryRow(dbContext, entry.Id, entry.MealDate, beforeJson, afterJson, actorUserId, now);
        await auditService.WriteAsync(
            new AuditEventEntry(
                beforeJson is null ? AuditEventTypes.CalendarCreated : AuditEventTypes.CalendarUpdated,
                AuditEntityTypes.CalendarEntry,
                EntityId: entry.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: afterJson,
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: MealDateMetadata(entry.MealDate)),
            cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var summary = fields.Recipe is null
            ? null
            : new CalendarRecipeSummary(
                fields.Recipe.Id,
                fields.Recipe.Name,
                null,
                EnumTextConverters.RecipeStatusToText(fields.Recipe.Status));
        return new CalendarMutationResult.Success(ToDayRecord(date, context, entry, summary, repeatWarning: null));
    }

    /// <summary>Hard-deletes a today/future entry after recording history and audit.</summary>
    public async Task<CalendarMutationResult> DeleteAsync(
        DateOnly date,
        long? actorUserId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (HouseholdClock.IsHistorical(date, context))
        {
            return PastDate();
        }

        var entry = await dbContext.CalendarEntries
            .FirstOrDefaultAsync(candidate => candidate.MealDate == date, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return new CalendarMutationResult.Failure(
                StatusCodes.Status404NotFound,
                ErrorCodes.NotFound,
                "There is no entry on this date.");
        }

        var beforeJson = SnapshotJson(entry);
        var mealDate = entry.MealDate;
        var entryId = entry.Id;

        // Entry, history, and audit commit atomically (docs/M12_PLAN.md §2.1).
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.CalendarEntries.Remove(entry);
        StageHistory(entryId: null, mealDate, beforeJson, afterJson: null, actorUserId);
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.CalendarDeleted,
                AuditEntityTypes.CalendarEntry,
                EntityId: entryId.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: MealDateMetadata(mealDate)),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CalendarMutationResult.Success(
            ToDayRecord(date, context, entry: null, recipe: null, repeatWarning: null));
    }

    /// <summary>Locks or unlocks a today/future entry.</summary>
    public async Task<CalendarMutationResult> SetLockAsync(
        DateOnly date,
        bool locked,
        long? actorUserId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (HouseholdClock.IsHistorical(date, context))
        {
            return PastDate();
        }

        var entry = await dbContext.CalendarEntries
            .FirstOrDefaultAsync(candidate => candidate.MealDate == date, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return new CalendarMutationResult.Failure(
                StatusCodes.Status422UnprocessableEntity,
                ErrorCodes.CalendarBlankCannotLock,
                "A blank date cannot be locked. Add an entry first.");
        }

        if (entry.IsLocked == locked)
        {
            return new CalendarMutationResult.Success(await DayAfterAsync(date, context, entry, cancellationToken)
                .ConfigureAwait(false));
        }

        var beforeJson = SnapshotJson(entry);
        entry.IsLocked = locked;
        entry.UpdatedAt = timeProvider.GetUtcNow();
        entry.UpdatedByUserId = actorUserId;

        // Entry, history, and audit commit atomically (docs/M12_PLAN.md §2.1).
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var afterJson = SnapshotJson(entry);
        StageHistory(entry.Id, entry.MealDate, beforeJson, afterJson, actorUserId);
        await auditService.WriteAsync(
            new AuditEventEntry(
                locked ? AuditEventTypes.CalendarLocked : AuditEventTypes.CalendarUnlocked,
                AuditEntityTypes.CalendarEntry,
                EntityId: entry.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: afterJson,
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: MealDateMetadata(entry.MealDate)),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new CalendarMutationResult.Success(await DayAfterAsync(date, context, entry, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Refreshes the frozen display snapshot of non-historical entries that
    /// reference a renamed recipe (docs/M5_PLAN.md §1.5). Historical entries are
    /// untouched. Called by the recipe write path inside its transaction.
    /// </summary>
    public Task RefreshSnapshotsForRecipeAsync(
        long recipeId,
        string displayName,
        CancellationToken cancellationToken = default) =>
        RefreshSnapshotsAsync(dbContext, clock, recipeId, displayName, cancellationToken);

    /// <summary>
    /// Shared refresh used by the recipe write path (which owns the transaction)
    /// and the public API above.
    /// </summary>
    public static async Task RefreshSnapshotsAsync(
        AppDbContext dbContext,
        HouseholdClock clock,
        long recipeId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var entries = await dbContext.CalendarEntries
            .Where(entry => entry.RecipeId == recipeId && entry.MealDate >= context.Today)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var entry in entries)
        {
            // Frozen history must never be refreshed (the boundary can make a
            // date historical even when it is not before "today").
            if (HouseholdClock.IsHistorical(entry.MealDate, context))
            {
                continue;
            }

            entry.SnapshotRecipeName = displayName;
            entry.SnapshotDisplayText = BuildDisplayText(
                entry.EntryType,
                entry.CustomLabel,
                entry.RestaurantName,
                entry.LocationText,
                displayName);
        }
    }

    /// <summary>Builds the display text using the documented precedence.</summary>
    public static string BuildDisplayText(
        CalendarEntryType type,
        string? customLabel,
        string? restaurantName,
        string? locationText,
        string? recipeName) =>
        recipeName
        ?? customLabel
        ?? restaurantName
        ?? locationText
        ?? TypeLabel(type);

    private static string TypeLabel(CalendarEntryType type) => type switch
    {
        CalendarEntryType.Recipe => "Recipe",
        CalendarEntryType.EatOut => "Eat Out",
        CalendarEntryType.FendForYourself => "Fend For Yourself",
        CalendarEntryType.HostFamilyDinner => "Host Family Dinner",
        CalendarEntryType.GoToFamilyDinner => "Go To Family Dinner",
        CalendarEntryType.Holiday => "Holiday",
        CalendarEntryType.Misc => "Misc",
        CalendarEntryType.Vacation => "Vacation",
        _ => "Meal",
    };

    private static bool IncludesGroceries(CalendarEntryType type, bool hasRecipe) => type switch
    {
        CalendarEntryType.Recipe => true,
        CalendarEntryType.HostFamilyDinner => hasRecipe,
        CalendarEntryType.Holiday => hasRecipe,
        CalendarEntryType.Misc => hasRecipe,
        _ => false,
    };

    private static CalendarEntryType? ReadEntryType(
        string? raw,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            errors["entry_type"] = ["entry_type is required."];
            return null;
        }

        if (!EnumTextConverters.TryCalendarEntryTypeFromText(raw, out var type))
        {
            errors["entry_type"] = ["entry_type is not a known value."];
            return null;
        }

        return type;
    }

    internal static string? RequiredCode(Dictionary<string, string[]> errors) =>
        errors.ContainsKey("recipe_id") ? ErrorCodes.CalendarRecipeRequired
        : errors.ContainsKey("custom_label") ? ErrorCodes.CalendarMiscLabelRequired
        : null;

    private static string? Sanitize(string? value, int maxLength) =>
        value is null ? null : ImportTextSanitizer.Sanitize(value, maxLength);

    private static CalendarMutationResult.Failure PastDate() =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.CalendarDateInPast,
            "Dates in the past are read-only. Use the admin historical correction workflow.");

    private async Task<CalendarDayRecord> DayAfterAsync(
        DateOnly date,
        HouseholdCalendarContext context,
        CalendarEntry entry,
        CancellationToken cancellationToken)
    {
        CalendarRecipeSummary? recipe = null;
        CalendarRepeatWarning? warning = null;
        if (entry.RecipeId is { } recipeId)
        {
            recipe = await LoadRecipeSummaryAsync(recipeId, cancellationToken).ConfigureAwait(false);
            warning = await ComputeRepeatWarningAsync(date, recipeId, cancellationToken).ConfigureAwait(false);
        }

        return ToDayRecord(date, context, entry, recipe, warning);
    }

    private CalendarDayRecord ToDayRecord(
        DateOnly date,
        HouseholdCalendarContext context,
        CalendarEntry? entry,
        CalendarRecipeSummary? recipe,
        CalendarRepeatWarning? repeatWarning)
    {
        var historical = HouseholdClock.IsHistorical(date, context);
        return new CalendarDayRecord(
            date,
            date == context.Today,
            historical,
            CanEdit: !historical,
            entry is null ? null : ToEntryRecord(entry, context, recipe, historical),
            repeatWarning);
    }

    private CalendarEntryRecord ToEntryRecord(
        CalendarEntry entry,
        HouseholdCalendarContext context,
        CalendarRecipeSummary? recipe,
        bool historical)
    {
        // Historical display is frozen to the stored snapshot; non-historical
        // display tracks the live recipe name so renames show immediately.
        var displayText = historical || recipe is null
            ? entry.SnapshotDisplayText
            : BuildDisplayText(
                entry.EntryType,
                entry.CustomLabel,
                entry.RestaurantName,
                entry.LocationText,
                recipe.Name);

        return new CalendarEntryRecord(
            entry.Id,
            EnumTextConverters.CalendarEntryTypeToText(entry.EntryType),
            recipe,
            displayText,
            entry.IsLocked,
            entry.DinerCount,
            entry.DinerCount ?? context.DefaultDinerCount,
            entry.RestaurantName,
            entry.LocationText,
            entry.CustomLabel,
            entry.Notes,
            entry.IconKey,
            entry.IncludeGroceries,
            entry.EntryType == CalendarEntryType.Vacation,
            entry.CreatedAt,
            entry.UpdatedAt);
    }

    private async Task<CalendarRecipeSummary?> LoadRecipeSummaryAsync(
        long recipeId,
        CancellationToken cancellationToken)
    {
        var recipe = await dbContext.Recipes
            .AsNoTracking()
            .Where(candidate => candidate.Id == recipeId)
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Status })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (recipe is null)
        {
            return null;
        }

        var hasImage = await dbContext.RecipeImages
            .AsNoTracking()
            .AnyAsync(image => image.RecipeId == recipeId, cancellationToken)
            .ConfigureAwait(false);

        return new CalendarRecipeSummary(
            recipe.Id,
            recipe.Name,
            hasImage ? $"/api/v1/recipes/{recipe.Id}/image" : null,
            EnumTextConverters.RecipeStatusToText(recipe.Status));
    }

    private async Task<IReadOnlyDictionary<long, CalendarRecipeSummary>> LoadRecipeSummariesAsync(
        long[] recipeIds,
        CancellationToken cancellationToken)
    {
        if (recipeIds.Length == 0)
        {
            return new Dictionary<long, CalendarRecipeSummary>();
        }

        var recipes = await dbContext.Recipes
            .AsNoTracking()
            .Where(candidate => recipeIds.Contains(candidate.Id))
            .Select(candidate => new { candidate.Id, candidate.Name, candidate.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var withImages = await dbContext.RecipeImages
            .AsNoTracking()
            .Where(image => recipeIds.Contains(image.RecipeId))
            .Select(image => image.RecipeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var imageSet = withImages.ToHashSet();

        return recipes.ToDictionary(
            recipe => recipe.Id,
            recipe => new CalendarRecipeSummary(
                recipe.Id,
                recipe.Name,
                imageSet.Contains(recipe.Id) ? $"/api/v1/recipes/{recipe.Id}/image" : null,
                EnumTextConverters.RecipeStatusToText(recipe.Status)));
    }

    private Task<CalendarRepeatWarning> ComputeRepeatWarningAsync(
        DateOnly date,
        long recipeId,
        CancellationToken cancellationToken) =>
        ComputeWarningAsync(dbContext, date, recipeId, cancellationToken);

    private static async Task<CalendarRepeatWarning> ComputeWarningAsync(
        AppDbContext dbContext,
        DateOnly date,
        long recipeId,
        CancellationToken cancellationToken)
    {
        var windowStart = date.AddDays(-RepeatLookbackDays);
        var windowEnd = date.AddDays(RepeatLookaheadDays);
        var occurrences = await LoadOccurrencesAsync(
            dbContext,
            windowStart,
            windowEnd,
            recipeId,
            excludeDate: date,
            cancellationToken).ConfigureAwait(false);

        return new CalendarRepeatWarning(recipeId, windowStart, windowEnd, occurrences);
    }

    private Task<IReadOnlyList<CalendarOccurrence>> LoadOccurrencesAsync(
        DateOnly windowStart,
        DateOnly windowEnd,
        long recipeId,
        DateOnly excludeDate,
        CancellationToken cancellationToken) =>
        LoadOccurrencesAsync(dbContext, windowStart, windowEnd, recipeId, excludeDate, cancellationToken);

    private static async Task<IReadOnlyList<CalendarOccurrence>> LoadOccurrencesAsync(
        AppDbContext dbContext,
        DateOnly windowStart,
        DateOnly windowEnd,
        long recipeId,
        DateOnly excludeDate,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.RecipeId == recipeId
                && entry.MealDate >= windowStart
                && entry.MealDate <= windowEnd
                && entry.MealDate != excludeDate)
            .OrderBy(entry => entry.MealDate)
            .Select(entry => new { entry.MealDate, entry.EntryType })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new CalendarOccurrence(
                row.MealDate,
                EnumTextConverters.CalendarEntryTypeToText(row.EntryType)))
            .ToArray();
    }

    /// <summary>
    /// Validates and sanitizes a calendar write request (docs/M5_PLAN.md §1.6).
    /// The normal upsert and the M12 historical correction call this one method,
    /// so the per-type field rules cannot drift (docs/M12_PLAN.md §2.1).
    /// Returns the resolved fields, or the per-field errors of an invalid request.
    /// </summary>
    internal static async Task<(CalendarWriteFields? Fields, Dictionary<string, string[]> Errors)> ValidateWriteRequestAsync(
        AppDbContext dbContext,
        CalendarWriteRequest request,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var type = ReadEntryType(request.EntryType, errors);

        var customLabel = Sanitize(request.CustomLabel, RecipeService.MaxNameLength);
        var restaurantName = Sanitize(request.RestaurantName, RecipeService.MaxNameLength);
        var locationText = Sanitize(request.LocationText, RecipeService.MaxNameLength);
        var notes = Sanitize(request.Notes, RecipeService.MaxLongTextLength);

        if (type == CalendarEntryType.Recipe && request.RecipeId is null)
        {
            errors["recipe_id"] = ["recipe_id is required for a recipe entry."];
        }

        if (type == CalendarEntryType.Misc && customLabel is null)
        {
            errors["custom_label"] = ["custom_label is required for a misc entry."];
        }

        if (type is { } recipeType
            && request.RecipeId is not null
            && recipeType is not (CalendarEntryType.Recipe
                or CalendarEntryType.HostFamilyDinner
                or CalendarEntryType.Holiday
                or CalendarEntryType.Misc))
        {
            errors["recipe_id"] = ["This entry type does not support a recipe."];
        }

        if (request.DinerCount is { } dinerCount
            && (dinerCount < MinDinerCount || dinerCount > MaxDinerCount))
        {
            errors["diner_count"] = [$"diner_count must be between {MinDinerCount} and {MaxDinerCount}."];
        }

        var iconKey = Sanitize(request.IconKey, RecipeService.MaxNameLength);
        if (iconKey is not null)
        {
            if (!CalendarIcons.IsKnown(iconKey))
            {
                errors["icon_key"] = ["icon_key is not a known icon."];
                iconKey = null;
            }
            else if (type is { } resolvedType && !IconTypes.Contains(resolvedType))
            {
                errors["icon_key"] = ["This entry type does not support an icon."];
                iconKey = null;
            }
        }

        Recipe? recipe = null;
        if (request.RecipeId is { } recipeId)
        {
            recipe = await dbContext.Recipes
                .FirstOrDefaultAsync(candidate => candidate.Id == recipeId, cancellationToken)
                .ConfigureAwait(false);
            if (recipe is null)
            {
                errors["recipe_id"] = ["The recipe does not exist."];
            }
        }

        if (errors.Count > 0 || type is not { } entryType)
        {
            return (null, errors);
        }

        var displayText = BuildDisplayText(
            entryType,
            customLabel,
            restaurantName,
            locationText,
            recipe?.Name);

        return (
            new CalendarWriteFields(
                entryType,
                recipe,
                request.DinerCount,
                restaurantName,
                locationText,
                customLabel,
                notes,
                iconKey,
                displayText,
                IncludesGroceries(entryType, recipe is not null)),
            errors);
    }

    /// <summary>
    /// Applies validated fields to an entry. The lock state is deliberately not
    /// touched, so a historical correction leaves a locked date locked
    /// (docs/M12_PLAN.md §1.9).
    /// </summary>
    internal static void ApplyFields(
        CalendarEntry entry,
        CalendarWriteFields fields,
        DateTimeOffset now,
        long? actorUserId)
    {
        entry.EntryType = fields.EntryType;
        entry.RecipeId = fields.Recipe?.Id;
        entry.DinerCount = fields.DinerCount;
        entry.RestaurantName = fields.RestaurantName;
        entry.LocationText = fields.LocationText;
        entry.CustomLabel = fields.CustomLabel;
        entry.Notes = fields.Notes;
        entry.IconKey = fields.IconKey;
        entry.IncludeGroceries = fields.IncludeGroceries;
        entry.SnapshotRecipeName = fields.Recipe?.Name;
        entry.SnapshotDisplayText = fields.DisplayText;
        entry.UpdatedAt = now;
        entry.UpdatedByUserId = actorUserId;
    }

    /// <summary>
    /// Stages one <see cref="CalendarEntryHistory"/> row on the caller's unit of
    /// work (docs/DATABASE.md §8). The normal calendar writes stage an ordinary
    /// row; the M12 historical correction stages a flagged row carrying the
    /// reason.
    /// </summary>
    internal static void StageHistoryRow(
        AppDbContext dbContext,
        long? entryId,
        DateOnly mealDate,
        string? beforeJson,
        string? afterJson,
        long? actorUserId,
        DateTimeOffset changedAt,
        bool isHistoricalCorrection = false,
        string? changeReason = null) =>
        dbContext.CalendarEntryHistory.Add(new CalendarEntryHistory
        {
            CalendarEntryId = entryId,
            MealDate = mealDate,
            OperationId = Guid.NewGuid(),
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            ChangedByUserId = actorUserId,
            ChangedAt = changedAt,
            ChangeReason = changeReason,
            IsHistoricalCorrection = isHistoricalCorrection,
        });
    /// <summary>Stages one history row; the caller's SaveChanges commits it.</summary>
    private void StageHistory(
        long? entryId,
        DateOnly mealDate,
        string? beforeJson,
        string? afterJson,
        long? actorUserId) =>
        StageHistoryRow(
            dbContext,
            entryId,
            mealDate,
            beforeJson,
            afterJson,
            actorUserId,
            timeProvider.GetUtcNow());

    internal static string SnapshotJson(CalendarEntry entry) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = entry.Id,
            ["meal_date"] = entry.MealDate.ToString("yyyy-MM-dd"),
            ["entry_type"] = EnumTextConverters.CalendarEntryTypeToText(entry.EntryType),
            ["recipe_id"] = entry.RecipeId,
            ["is_locked"] = entry.IsLocked,
            ["diner_count"] = entry.DinerCount,
            ["restaurant_name"] = entry.RestaurantName,
            ["location_text"] = entry.LocationText,
            ["custom_label"] = entry.CustomLabel,
            ["notes"] = entry.Notes,
            ["icon_key"] = entry.IconKey,
            ["include_groceries"] = entry.IncludeGroceries,
            ["snapshot_recipe_name"] = entry.SnapshotRecipeName,
            ["snapshot_display_text"] = entry.SnapshotDisplayText,
        });

    /// <summary>
    /// Audit metadata naming the audited date target (the entry id changes on
    /// create/delete, so the date is the stable target discriminator).
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> MealDateMetadata(DateOnly mealDate) =>
        new Dictionary<string, object?>
        {
            ["meal_date"] = mealDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
}
