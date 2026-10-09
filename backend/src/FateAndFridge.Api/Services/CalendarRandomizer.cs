using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>Outcome of a single-date reroll (docs/M6_PLAN.md §1.11).</summary>
public abstract record CalendarRerollOutcome
{
    /// <summary>The reroll committed; the day reflects the stored state.</summary>
    public sealed record Success(CalendarDayRecord Day, Guid OperationId) : CalendarRerollOutcome;

    /// <summary>A non-validation failure (past date, locked, vacation, no candidates).</summary>
    public sealed record Failure(int StatusCode, string Code, string Message) : CalendarRerollOutcome;
}

/// <summary>Outcome of a single-date undo (docs/M6_PLAN.md §1.13).</summary>
public abstract record CalendarUndoOutcome
{
    /// <summary>The undo committed; the day reflects the restored state.</summary>
    public sealed record Success(CalendarDayRecord Day) : CalendarUndoOutcome;

    /// <summary>A non-validation failure (missing/already undone, or a conflicting later change).</summary>
    public sealed record Failure(int StatusCode, string Code, string Message) : CalendarUndoOutcome;
}

/// <summary>
/// M6 single-date reroll (docs/M6_PLAN.md §1.11, docs/RANDOMIZER.md §13). Builds
/// the scoring context from the stored calendar (candidates, exact-repeat
/// occurrences, protein neighbors), calls the pure <see cref="RandomizerEngine"/>,
/// and persists the replacement plus exactly one history row atomically. It
/// broadcasts nothing; live sync arrives in M9.
/// </summary>
public sealed class CalendarRandomizer(
    AppDbContext dbContext,
    HouseholdClock clock,
    CalendarService calendar,
    RandomizerEngine engine,
    TimeProvider timeProvider)
{
    /// <summary>Documented defaults used only if no household settings row exists (docs/M6_PLAN.md §1.3).</summary>
    private static readonly RandomizerOptions DefaultOptions =
        new(7, 14, 0.10m, 0.02m, 0.70m, 1, 1.10m, 3, 8, 90);

    private static readonly IReadOnlySet<string> EmptyProteinKeys =
        new HashSet<string>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// Rerolls one date: fills a blank date or replaces any non-locked,
    /// non-vacation entry with a randomly selected Normal recipe.
    /// </summary>
    public async Task<CalendarRerollOutcome> RerollAsync(
        DateOnly date,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (HouseholdClock.IsHistorical(date, context))
        {
            return PastDate();
        }

        // Serialize concurrent mutations of the same date (docs/M6_PLAN.md
        // §1.11): the transaction-scoped advisory lock is released on
        // commit/rollback, so a second concurrent reroll waits here and then
        // re-reads the committed entry instead of racing it (blank-date
        // double-insert, lost updates).
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockDateAsync(date, cancellationToken).ConfigureAwait(false);

        var existing = await dbContext.CalendarEntries
            .FirstOrDefaultAsync(entry => entry.MealDate == date, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.IsLocked)
            {
                return new CalendarRerollOutcome.Failure(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.CalendarEntryLocked,
                    "This date is locked. Unlock it before rerolling.");
            }

            if (existing.EntryType == CalendarEntryType.Vacation)
            {
                return new CalendarRerollOutcome.Failure(
                    StatusCodes.Status422UnprocessableEntity,
                    ErrorCodes.RandomizerNotApplicable,
                    "A vacation day cannot be rerolled.");
            }
        }

        var settings = await dbContext.HouseholdSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var options = settings is null ? DefaultOptions : RandomizerOptions.From(settings);

        var candidateRows = await dbContext.Recipes
            .AsNoTracking()
            .Where(recipe => recipe.Status == RecipeStatus.Normal)
            .Select(recipe => new { recipe.Id, recipe.IsFavorite })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidateRows.Count == 0)
        {
            return new CalendarRerollOutcome.Failure(
                StatusCodes.Status422UnprocessableEntity,
                ErrorCodes.RandomizerNoCandidates,
                "No Normal recipes are available to select.");
        }

        var candidateIds = candidateRows.Select(row => row.Id).ToArray();
        var candidateProteins = await LoadProteinKeysAsync(candidateIds, cancellationToken).ConfigureAwait(false);
        var candidates = candidateRows
            .Select(row => new RandomizerCandidate(
                row.Id,
                row.IsFavorite,
                ProteinKeys(candidateProteins, row.Id)))
            .ToArray();

        // The occurrence read covers the union of the exact-repeat and spacing
        // windows; the engine applies each window's own bounds.
        var range = Math.Max(
            options.RepeatLookbackDays,
            Math.Max(options.RepeatLookaheadDays, options.SpacingSearchDays));
        var entryRows = await dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.RecipeId != null
                && entry.MealDate >= date.AddDays(-range)
                && entry.MealDate <= date.AddDays(range)
                && entry.MealDate != date)
            .Select(entry => new { RecipeId = entry.RecipeId!.Value, entry.MealDate, entry.EntryType })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var occurrences = entryRows
            .Select(row => new RandomizerOccurrence(row.RecipeId, row.MealDate, IsSpecialDay(row.EntryType)))
            .ToArray();

        var neighborRows = await dbContext.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.RecipeId != null
                && entry.MealDate >= date.AddDays(-options.ProteinNeighborDistance)
                && entry.MealDate <= date.AddDays(options.ProteinNeighborDistance)
                && entry.MealDate != date)
            .Select(entry => new { RecipeId = entry.RecipeId!.Value, entry.MealDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var neighborRecipeIds = neighborRows.Select(row => row.RecipeId).Distinct().ToArray();
        var neighborProteins = await LoadProteinKeysAsync(neighborRecipeIds, cancellationToken).ConfigureAwait(false);
        var neighbors = neighborRows
            .Select(row => new RandomizerNeighbor(row.MealDate, ProteinKeys(neighborProteins, row.RecipeId)))
            .ToArray();

        var result = engine.Select(
            options,
            new RandomizerRequest(date, candidates, occurrences, neighbors));

        var recipe = await dbContext.Recipes
            .FirstAsync(candidate => candidate.Id == result.RecipeId, cancellationToken)
            .ConfigureAwait(false);

        var operationId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        string? beforeJson = null;
        CalendarEntry entry;
        if (existing is null)
        {
            entry = new CalendarEntry
            {
                MealDate = date,
                SnapshotDisplayText = recipe.Name,
                CreatedAt = now,
                CreatedByUserId = actorUserId,
            };
            dbContext.CalendarEntries.Add(entry);
        }
        else
        {
            entry = existing;
            beforeJson = CalendarService.SnapshotJson(entry);
        }

        // The replacement resets entry metadata (docs/M6_PLAN.md §1.11).
        entry.EntryType = CalendarEntryType.Recipe;
        entry.RecipeId = recipe.Id;
        entry.IsLocked = false;
        entry.DinerCount = null;
        entry.RestaurantName = null;
        entry.LocationText = null;
        entry.CustomLabel = null;
        entry.Notes = null;
        entry.IconKey = null;
        entry.IncludeGroceries = true;
        entry.SnapshotRecipeName = recipe.Name;
        entry.SnapshotDisplayText = recipe.Name;
        entry.UpdatedAt = now;
        entry.UpdatedByUserId = actorUserId;

        // Entry + exactly one history row commit atomically inside the same
        // transaction that holds the per-date lock. The second SaveChanges makes
        // a freshly created entry's id available to history (same pattern as
        // CalendarService.UpsertAsync).
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        dbContext.CalendarEntryHistory.Add(new CalendarEntryHistory
        {
            CalendarEntryId = entry.Id,
            MealDate = date,
            OperationId = operationId,
            BeforeJson = beforeJson,
            AfterJson = CalendarService.SnapshotJson(entry),
            MetadataJson = SerializeMetadata(result.Metadata),
            ChangedByUserId = actorUserId,
            ChangedAt = now,
            IsHistoricalCorrection = false,
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var day = await calendar.GetDayAsync(date, cancellationToken).ConfigureAwait(false);
        return new CalendarRerollOutcome.Success(day, operationId);
    }

    /// <summary>
    /// Serializes the score breakdown to the documented snake_case shape
    /// (docs/M6_PLAN.md §2.3), including <c>"kind":"reroll"</c>.
    /// </summary>
    public static string SerializeMetadata(RandomizerMetadata metadata) =>
        JsonSerializer.Serialize(
            new RerollMetadataDocument(
                "reroll",
                metadata.CandidateCount,
                metadata.ChosenRecipeId,
                metadata.BaseWeight,
                metadata.RepeatMultiplier,
                metadata.RepeatOccurrences,
                metadata.ProteinMultiplier,
                metadata.SharedProteins,
                metadata.SpacingDays,
                metadata.SpacingMultiplier,
                metadata.FavoriteMultiplier,
                metadata.FinalWeight),
            MetadataJsonOptions);

    private static bool IsSpecialDay(CalendarEntryType type) =>
        type is CalendarEntryType.Holiday
            or CalendarEntryType.HostFamilyDinner
            or CalendarEntryType.Misc;

    private static IReadOnlySet<string> ProteinKeys(
        IReadOnlyDictionary<long, HashSet<string>> proteinKeys,
        long recipeId) =>
        proteinKeys.TryGetValue(recipeId, out var keys) ? keys : EmptyProteinKeys;

    private static CalendarRerollOutcome.Failure PastDate() =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.CalendarDateInPast,
            "Dates in the past are read-only. Use the admin historical correction workflow.");

    private static CalendarUndoOutcome.Failure UndoPastDate() =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.CalendarDateInPast,
            "Dates in the past are read-only. Use the admin historical correction workflow.");

    /// <summary>
    /// Takes the per-date transaction-scoped PostgreSQL advisory lock so
    /// concurrent reroll/undo of the same date serialize. The lock is released
    /// automatically when the caller's transaction commits or rolls back.
    /// </summary>
    private Task LockDateAsync(DateOnly date, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { (long)date.DayNumber },
            cancellationToken);

    /// <summary>
    /// True when a history row's <c>metadata_json</c> is a reroll document
    /// (<c>{"kind":"reroll",…}</c>, docs/M6_PLAN.md §2.3). Missing or malformed
    /// metadata is not an undoable reroll.
    /// </summary>
    private static bool IsRerollMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && string.Equals(kind.GetString(), "reroll", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<long, HashSet<string>>> LoadProteinKeysAsync(
        long[] recipeIds,
        CancellationToken cancellationToken)
    {
        if (recipeIds.Length == 0)
        {
            return new Dictionary<long, HashSet<string>>();
        }

        var rows = await dbContext.RecipeProteins
            .AsNoTracking()
            .Where(link => recipeIds.Contains(link.RecipeId))
            .Join(
                dbContext.Proteins.AsNoTracking(),
                link => link.ProteinId,
                protein => protein.Id,
                (link, protein) => new { link.RecipeId, protein.NormalizedName })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new Dictionary<long, HashSet<string>>();
        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.RecipeId, out var keys))
            {
                keys = new HashSet<string>(StringComparer.Ordinal);
                result[row.RecipeId] = keys;
            }

            keys.Add(row.NormalizedName);
        }

        return result;
    }

    /// <summary>
    /// Undoes one reroll operation (docs/M6_PLAN.md §1.13): restores the exact
    /// pre-reroll state (the prior entry, or blank if the date was empty) and
    /// stages exactly one history row. A missing, non-reroll, or already-undone
    /// operation fails <c>UNDO_EXPIRED</c>; a later change to the date fails
    /// <c>UNDO_CONFLICT</c>; a date that has since become historical fails
    /// <c>CALENDAR_DATE_IN_PAST</c> (past dates are immutable in normal use).
    /// </summary>
    public async Task<CalendarUndoOutcome> UndoAsync(
        Guid operationId,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var reroll = await dbContext.CalendarEntryHistory
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.OperationId == operationId, cancellationToken)
            .ConfigureAwait(false);

        // Only a reroll operation is undoable; a missing row or any other kind
        // of history entry (e.g. a later upsert) is not a reroll to restore.
        if (reroll is null || !IsRerollMetadata(reroll.MetadataJson))
        {
            return Expired();
        }

        // Past dates are immutable in normal use (AGENTS.md): undo restores
        // prior state, but it may not rewrite a date that has since become
        // historical.
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (HouseholdClock.IsHistorical(reroll.MealDate, context))
        {
            return UndoPastDate();
        }

        // Serialize concurrent mutations of the same date (docs/M6_PLAN.md
        // §1.11): the transaction-scoped advisory lock is released on
        // commit/rollback, so a concurrent second undo re-checks "already
        // undone" after the first commits instead of double-applying.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockDateAsync(reroll.MealDate, cancellationToken).ConfigureAwait(false);

        // The undo row carries this reason; its presence means the operation was
        // already undone, making a second call UNDO_EXPIRED (idempotent-safe).
        var undoReason = UndoChangeReason(operationId);
        var alreadyUndone = await dbContext.CalendarEntryHistory
            .AsNoTracking()
            .AnyAsync(row => row.ChangeReason == undoReason, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyUndone)
        {
            return Expired();
        }

        // The reroll must still be the latest change for the meal date; any
        // later lock/upsert/delete makes the undo ambiguous.
        var latest = await dbContext.CalendarEntryHistory
            .AsNoTracking()
            .Where(row => row.MealDate == reroll.MealDate)
            .OrderByDescending(row => row.ChangedAt)
            .ThenByDescending(row => row.Id)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest.Id != reroll.Id)
        {
            return new CalendarUndoOutcome.Failure(
                StatusCodes.Status409Conflict,
                ErrorCodes.UndoConflict,
                "This date has changed since the reroll. Undo is no longer available.");
        }

        var entry = await dbContext.CalendarEntries
            .FirstOrDefaultAsync(candidate => candidate.MealDate == reroll.MealDate, cancellationToken)
            .ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        var undoBeforeJson = entry is null ? null : CalendarService.SnapshotJson(entry);

        long? restoredEntryId;
        string? restoredAfterJson;
        if (reroll.BeforeJson is null)
        {
            // The date was blank before the reroll: restoring means deleting the
            // entry the reroll created.
            if (entry is not null)
            {
                dbContext.CalendarEntries.Remove(entry);
            }

            restoredEntryId = null;
            restoredAfterJson = null;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (entry is null)
            {
                // Defensive: the reroll's snapshot proves an entry existed.
                entry = new CalendarEntry
                {
                    MealDate = reroll.MealDate,
                    SnapshotDisplayText = string.Empty,
                    CreatedAt = now,
                    CreatedByUserId = actorUserId,
                };
                dbContext.CalendarEntries.Add(entry);
            }

            ApplySnapshot(entry, reroll.BeforeJson);
            entry.UpdatedAt = now;
            entry.UpdatedByUserId = actorUserId;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            restoredEntryId = entry.Id;
            restoredAfterJson = CalendarService.SnapshotJson(entry);
        }

        dbContext.CalendarEntryHistory.Add(new CalendarEntryHistory
        {
            CalendarEntryId = restoredEntryId,
            MealDate = reroll.MealDate,
            OperationId = Guid.NewGuid(),
            BeforeJson = undoBeforeJson,
            AfterJson = restoredAfterJson,
            MetadataJson = SerializeUndoMetadata(operationId),
            ChangedByUserId = actorUserId,
            ChangedAt = now,
            ChangeReason = undoReason,
            IsHistoricalCorrection = false,
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var day = await calendar.GetDayAsync(reroll.MealDate, cancellationToken).ConfigureAwait(false);
        return new CalendarUndoOutcome.Success(day);
    }

    /// <summary>
    /// Serializes undo metadata in the documented snake_case shape:
    /// <c>{"kind":"undo","undone_operation_id":"..."}</c>.
    /// </summary>
    public static string SerializeUndoMetadata(Guid undoneOperationId) =>
        JsonSerializer.Serialize(
            new UndoMetadataDocument("undo", undoneOperationId),
            MetadataJsonOptions);

    private static string UndoChangeReason(Guid operationId) => $"undo:{operationId:D}";

    private static CalendarUndoOutcome.Failure Expired() =>
        new(
            StatusCodes.Status410Gone,
            ErrorCodes.UndoExpired,
            "This operation is missing or has already been undone.");

    /// <summary>
    /// Applies every restorable field from a stored snapshot document. Shared
    /// with the M7 batch undo so both undo paths restore the exact same fields.
    /// </summary>
    internal static void ApplySnapshot(CalendarEntry entry, string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        if (root.TryGetProperty("entry_type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            && EnumTextConverters.TryCalendarEntryTypeFromText(typeElement.GetString(), out var entryType))
        {
            entry.EntryType = entryType;
        }

        entry.RecipeId = ReadLong(root, "recipe_id");
        entry.IsLocked = ReadBool(root, "is_locked");
        entry.DinerCount = ReadInt(root, "diner_count");
        entry.RestaurantName = ReadString(root, "restaurant_name");
        entry.LocationText = ReadString(root, "location_text");
        entry.CustomLabel = ReadString(root, "custom_label");
        entry.Notes = ReadString(root, "notes");
        entry.IconKey = ReadString(root, "icon_key");
        entry.IncludeGroceries = ReadBool(root, "include_groceries");
        entry.SnapshotRecipeName = ReadString(root, "snapshot_recipe_name");
        entry.SnapshotDisplayText = ReadString(root, "snapshot_display_text") ?? string.Empty;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var value)
            ? value
            : null;

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
            ? value
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.True;

    /// <summary>Snake_case serialization shape for undo metadata (docs/M6_PLAN.md §2.3).</summary>
    private sealed record UndoMetadataDocument(string Kind, Guid UndoneOperationId);

    /// <summary>Snake_case serialization shape for <c>metadata_json</c> (docs/M6_PLAN.md §2.3).</summary>
    private sealed record RerollMetadataDocument(
        string Kind,
        int CandidateCount,
        long ChosenRecipeId,
        decimal BaseWeight,
        decimal RepeatMultiplier,
        int RepeatOccurrences,
        decimal ProteinMultiplier,
        IReadOnlyList<string> SharedProteins,
        int? SpacingDays,
        decimal SpacingMultiplier,
        decimal FavoriteMultiplier,
        decimal FinalWeight);
}
