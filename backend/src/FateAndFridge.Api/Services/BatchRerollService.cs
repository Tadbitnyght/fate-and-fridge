using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>Outcome of a batch reroll preview (docs/M7_PLAN.md §1.3–§1.7).</summary>
public abstract record BatchPreviewOutcome
{
    /// <summary>The preview draft was persisted; <see cref="Days"/> is the ordered diff.</summary>
    public sealed record Success(CalendarRerollDraft Draft, IReadOnlyList<BatchPreviewDay> Days) : BatchPreviewOutcome;

    /// <summary>A non-validation failure (past date, horizon, no candidates).</summary>
    public sealed record Failure(int StatusCode, string Code, string Message) : BatchPreviewOutcome;
}

/// <summary>Outcome of a batch reroll apply (docs/M7_PLAN.md §1.8–§1.12).</summary>
public abstract record BatchApplyOutcome
{
    /// <summary>The batch committed; <see cref="Days"/> are the changed day documents.</summary>
    public sealed record Success(Guid BatchOperationId, IReadOnlyList<CalendarDayRecord> Days) : BatchApplyOutcome;

    /// <summary>A non-validation failure (expired draft, conflict, not found).</summary>
    public sealed record Failure(int StatusCode, string Code, string Message) : BatchApplyOutcome;
}

/// <summary>Outcome of a batch undo (docs/M7_PLAN.md §1.10).</summary>
public abstract record BatchUndoOutcome
{
    /// <summary>The batch was reverted; <see cref="Days"/> are the restored day documents, in date order.</summary>
    public sealed record Success(IReadOnlyList<CalendarDayRecord> Days) : BatchUndoOutcome;

    /// <summary>A non-validation failure (missing/already undone, conflict, or a now-historical date).</summary>
    public sealed record Failure(int StatusCode, string Code, string Message) : BatchUndoOutcome;
}

/// <summary>One day in a batch preview diff.</summary>
public sealed record BatchPreviewDay(
    DateOnly Date,
    string Status,
    string? UnchangedReason,
    CalendarEntryRecord? Current,
    BatchProposedEntry? Proposed);

/// <summary>The recipe proposed for a day (M7 proposes plain <c>recipe</c> entries).</summary>
public sealed record BatchProposedEntry(string EntryType, long RecipeId, string DisplayText);

/// <summary>
/// M7 batch reroll preview (docs/M7_PLAN.md §1.3–§1.7, docs/RANDOMIZER.md §9–§12).
/// Builds a date range, runs the sequential engine pass in ascending date order
/// (earlier picks in the same preview count as occurrences/neighbors for later
/// dates), persists one draft with a 15-minute TTL, and returns the diff. Preview
/// never creates or modifies <c>calendar_entries</c> or
/// <c>calendar_entry_history</c> rows.
/// </summary>
public sealed class BatchRerollService(
    AppDbContext db,
    HouseholdClock clock,
    CalendarService calendar,
    RandomizerEngine engine,
    TimeProvider timeProvider)
{
    public const string ModeSevenDays = "seven_days";

    public const string ModeRestOfMonth = "rest_of_month";

    public const string ModeFillEmpty = "fill_empty";

    /// <summary>Server-side preview draft lifetime (docs/M7_PLAN.md §1.7).</summary>
    public static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(15);

    private static readonly string[] Modes = [ModeSevenDays, ModeRestOfMonth, ModeFillEmpty];

    /// <summary>Documented defaults used only if no household settings row exists.</summary>
    private static readonly RandomizerOptions DefaultOptions =
        new(7, 14, 0.10m, 0.02m, 0.70m, 1, 1.10m, 3, 8, 90);

    private static readonly IReadOnlySet<string> EmptyProteinKeys =
        new HashSet<string>(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string StatusProposed = "proposed";
    private const string StatusUnchanged = "unchanged";
    private const string ActionPropose = "propose";
    private const string ReasonLocked = "locked";
    private const string ReasonVacation = "vacation";
    private const string ReasonSpecial = "special";
    private const string ReasonExistingNonRecipe = "existing_non_recipe";

    /// <summary>True when <paramref name="mode"/> is a supported preview mode.</summary>
    public static bool IsKnownMode(string? mode) =>
        mode is not null && Array.IndexOf(Modes, mode) >= 0;

    /// <summary>
    /// Builds the preview for <paramref name="startDate"/> and persists the draft.
    /// </summary>
    public async Task<BatchPreviewOutcome> PreviewAsync(
        DateOnly startDate,
        string mode,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (!IsKnownMode(mode))
        {
            return new BatchPreviewOutcome.Failure(
                StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed,
                $"mode must be one of {string.Join(", ", Modes)}.");
        }

        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (HouseholdClock.IsHistorical(startDate, context))
        {
            return PastDate();
        }

        var endDate = ResolveEndDate(startDate, mode);
        if (!WithinHorizon(startDate, context.Today) || !WithinHorizon(endDate, context.Today))
        {
            return new BatchPreviewOutcome.Failure(
                StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed,
                $"The range must be within {HouseholdClock.HorizonYears} years of today.");
        }

        var settings = await db.HouseholdSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var options = settings is null ? DefaultOptions : RandomizerOptions.From(settings);

        var candidateRows = await db.Recipes
            .AsNoTracking()
            .Where(recipe => recipe.Status == RecipeStatus.Normal)
            .Select(recipe => new { recipe.Id, recipe.Name, recipe.IsFavorite })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidateRows.Count == 0)
        {
            return new BatchPreviewOutcome.Failure(
                StatusCodes.Status422UnprocessableEntity,
                ErrorCodes.RandomizerNoCandidates,
                "No Normal recipes are available to select.");
        }

        var candidateIds = candidateRows.Select(row => row.Id).ToArray();

        // The occurrence read covers the union of the exact-repeat and spacing
        // windows plus the protein-neighbor window, so each date's request is
        // built from a single read of the stored calendar.
        var occurrenceRadius = Math.Max(
            options.RepeatLookbackDays,
            Math.Max(options.RepeatLookaheadDays, options.SpacingSearchDays));
        var neighborRadius = Math.Max(options.ProteinNeighborDistance, 0);
        var contextRadius = Math.Max(occurrenceRadius, neighborRadius);
        var contextStart = startDate.AddDays(-contextRadius);
        var contextEnd = endDate.AddDays(contextRadius);

        var contextEntries = await db.CalendarEntries
            .AsNoTracking()
            .Where(entry => entry.MealDate >= contextStart && entry.MealDate <= contextEnd)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var contextRecipeIds = contextEntries
            .Where(entry => entry.RecipeId is not null)
            .Select(entry => entry.RecipeId!.Value)
            .Distinct()
            .ToArray();
        var allRecipeIds = candidateIds.Concat(contextRecipeIds).Distinct().ToArray();
        var proteinKeys = await LoadProteinKeysAsync(allRecipeIds, cancellationToken).ConfigureAwait(false);

        var candidates = candidateRows
            .Select(row => new RandomizerCandidate(row.Id, row.IsFavorite, ProteinKeys(proteinKeys, row.Id)))
            .ToArray();
        var candidateNames = candidateRows.ToDictionary(row => row.Id, row => row.Name);

        var range = await calendar.GetRangeAsync(startDate, endDate, cancellationToken).ConfigureAwait(false);
        var currentByDate = new Dictionary<DateOnly, CalendarEntryRecord>();
        foreach (var day in range.Entries)
        {
            if (day.Entry is not null)
            {
                currentByDate[day.Date] = day.Entry;
            }
        }

        var now = timeProvider.GetUtcNow();
        var previewDays = new List<BatchPreviewDay>();
        var draftDays = new List<ProposalDayDocument>();
        var proposedByDate = new Dictionary<DateOnly, long>();
        var replacedDates = new HashSet<DateOnly>();

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            currentByDate.TryGetValue(date, out var current);
            var decision = Decide(current, mode);
            if (!decision.Propose)
            {
                previewDays.Add(new BatchPreviewDay(date, StatusUnchanged, decision.Reason, current, null));
                draftDays.Add(ProposalDayDocument.Unchanged(date, decision.Reason, current));
                continue;
            }

            var occurrences = BuildOccurrences(
                contextEntries, proposedByDate, replacedDates, date, occurrenceRadius);
            var neighbors = BuildNeighbors(
                contextEntries, proposedByDate, replacedDates, proteinKeys, date, neighborRadius);

            var result = engine.Select(
                options,
                new RandomizerRequest(date, candidates, occurrences, neighbors));

            proposedByDate[date] = result.RecipeId;
            replacedDates.Add(date);

            previewDays.Add(new BatchPreviewDay(
                date,
                StatusProposed,
                null,
                current,
                new BatchProposedEntry("recipe", result.RecipeId, candidateNames[result.RecipeId])));
            draftDays.Add(ProposalDayDocument.Proposed(date, result.RecipeId, current, result.Metadata, mode));
        }

        var draft = new CalendarRerollDraft
        {
            Token = Guid.NewGuid(),
            Mode = mode,
            StartDate = startDate,
            EndDate = endDate,
            ActorUserId = actorUserId,
            ProposalJson = JsonSerializer.Serialize(
                new ProposalDocument(mode, startDate, endDate, draftDays),
                ProposalJsonOptions),
            CreatedAt = now,
            ExpiresAt = now + PreviewTtl,
            AppliedAt = null,
        };

        // Try Another supersedes the actor's previous unapplied draft(s); the
        // delete + insert commit atomically. No calendar state is touched.
        var staleDrafts = await db.CalendarRerollDrafts
            .Where(candidate => candidate.ActorUserId == actorUserId && candidate.AppliedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        db.CalendarRerollDrafts.RemoveRange(staleDrafts);
        db.CalendarRerollDrafts.Add(draft);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new BatchPreviewOutcome.Success(draft, previewDays);
    }

    /// <summary>
    /// Applies a stored preview draft atomically (docs/M7_PLAN.md §1.8–§1.12).
    /// One transaction acquires the per-date advisory locks in ascending order,
    /// re-checks every proposed target date against the stored before-state, and
    /// writes the previewed recipes plus exactly one history row per changed date
    /// sharing a single batch operation id. A missing/expired/consumed draft fails
    /// <c>BATCH_DRAFT_EXPIRED</c>; a changed target date fails <c>BATCH_CONFLICT</c>
    /// and writes nothing; another actor's draft is reported as <c>NOT_FOUND</c>.
    /// No live-sync broadcast and no audit rows are written (M9/M12).
    /// </summary>
    public async Task<BatchApplyOutcome> ApplyAsync(
        Guid token,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var draft = await db.CalendarRerollDrafts
            .FirstOrDefaultAsync(candidate => candidate.Token == token, cancellationToken)
            .ConfigureAwait(false);
        if (draft is null)
        {
            return DraftExpired();
        }

        // Drafts are per-actor: never reveal (or apply) another actor's draft.
        // The system actor (null caller) counts as a match for any owner.
        if (draft.ActorUserId is { } owner
            && actorUserId is { } caller
            && owner != caller)
        {
            return new BatchApplyOutcome.Failure(
                StatusCodes.Status404NotFound,
                ErrorCodes.NotFound,
                "There is no batch preview for this token.");
        }

        var now = timeProvider.GetUtcNow();
        if (draft.AppliedAt is not null || now > draft.ExpiresAt)
        {
            return DraftExpired();
        }

        var proposal = ReadProposal(draft.ProposalJson);
        if (proposal is null)
        {
            return DraftExpired();
        }

        var proposed = proposal.Days
            .Where(day => day.Action == ActionPropose && day.RecipeId is not null)
            .OrderBy(day => day.Date)
            .ToArray();

        // One transaction owns every lock, write, and the draft consumption; any
        // early return disposes it and rolls everything back.
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Serialize per target date with the same advisory lock as M6, acquired
        // in ascending date order to avoid deadlocks (docs/M7_PLAN.md §1.12).
        foreach (var day in proposed)
        {
            await LockDateAsync(day.Date, cancellationToken).ConfigureAwait(false);
        }

        // Re-check every target date against the before-state captured at preview
        // time; a single mismatch aborts the whole apply (docs/M7_PLAN.md §1.8).
        var currentByDate = new Dictionary<DateOnly, CalendarEntry?>();
        foreach (var day in proposed)
        {
            var current = await db.CalendarEntries
                .FirstOrDefaultAsync(entry => entry.MealDate == day.Date, cancellationToken)
                .ConfigureAwait(false);
            if (!MatchesBeforeState(current, day))
            {
                return Conflict();
            }

            currentByDate[day.Date] = current;
        }

        // Apply commits exactly the previewed recipes; load their display names
        // (the frozen proposal stores ids and the score breakdown only).
        var recipeIds = proposed.Select(day => day.RecipeId!.Value).Distinct().ToArray();
        var recipeNames = recipeIds.Length == 0
            ? new Dictionary<long, string>()
            : await db.Recipes
                .AsNoTracking()
                .Where(recipe => recipeIds.Contains(recipe.Id))
                .Select(recipe => new { recipe.Id, recipe.Name })
                .ToDictionaryAsync(recipe => recipe.Id, recipe => recipe.Name, cancellationToken)
                .ConfigureAwait(false);
        if (proposed.Any(day => !recipeNames.ContainsKey(day.RecipeId!.Value)))
        {
            return Conflict();
        }

        var batchOperationId = Guid.NewGuid();
        var history = new List<(CalendarEntry Entry, DateOnly Date, string? BeforeJson, string? MetadataJson)>();

        foreach (var day in proposed)
        {
            var current = currentByDate[day.Date];
            var recipeName = recipeNames[day.RecipeId!.Value];
            string? beforeJson = null;
            CalendarEntry entry;
            if (current is null)
            {
                entry = new CalendarEntry
                {
                    MealDate = day.Date,
                    SnapshotDisplayText = recipeName,
                    CreatedAt = now,
                    CreatedByUserId = actorUserId,
                };
                db.CalendarEntries.Add(entry);
            }
            else
            {
                entry = current;
                beforeJson = CalendarService.SnapshotJson(entry);
            }

            // The replacement resets entry metadata exactly like single reroll
            // (docs/M7_PLAN.md §1.11).
            entry.EntryType = CalendarEntryType.Recipe;
            entry.RecipeId = day.RecipeId!.Value;
            entry.IsLocked = false;
            entry.DinerCount = null;
            entry.RestaurantName = null;
            entry.LocationText = null;
            entry.CustomLabel = null;
            entry.Notes = null;
            entry.IconKey = null;
            entry.IncludeGroceries = true;
            entry.SnapshotRecipeName = recipeName;
            entry.SnapshotDisplayText = recipeName;
            entry.UpdatedAt = now;
            entry.UpdatedByUserId = actorUserId;

            history.Add((entry, day.Date, beforeJson, day.MetadataJson));
        }

        // First save assigns ids to freshly created entries so each history row
        // can reference its entry (same pattern as CalendarRandomizer).
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (entry, date, beforeJson, metadataJson) in history)
        {
            db.CalendarEntryHistory.Add(new CalendarEntryHistory
            {
                CalendarEntryId = entry.Id,
                MealDate = date,
                OperationId = Guid.NewGuid(),
                BatchOperationId = batchOperationId,
                BeforeJson = beforeJson,
                AfterJson = CalendarService.SnapshotJson(entry),
                MetadataJson = metadataJson,
                ChangedByUserId = actorUserId,
                ChangedAt = now,
                IsHistoricalCorrection = false,
            });
        }

        draft.AppliedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var days = new List<CalendarDayRecord>(proposed.Length);
        foreach (var day in proposed)
        {
            days.Add(await calendar.GetDayAsync(day.Date, cancellationToken).ConfigureAwait(false));
        }

        return new BatchApplyOutcome.Success(batchOperationId, days);
    }

    /// <summary>
    /// Undoes an entire batch apply (docs/M7_PLAN.md §1.10): reverts every date
    /// the apply changed to its exact prior state (the previous entry, or blank),
    /// but only while each affected date is still exactly as the apply left it.
    /// A missing or already-undone batch fails <c>UNDO_EXPIRED</c>; a later change
    /// to any affected date fails <c>UNDO_CONFLICT</c> and reverts nothing; a date
    /// that has since become historical fails <c>CALENDAR_DATE_IN_PAST</c>. One
    /// transaction acquires the per-date advisory locks in ascending date order and
    /// writes exactly one undo history row per restored date sharing a fresh
    /// <c>batch_operation_id</c>. No audit rows and no broadcast (M12/M9).
    /// </summary>
    public async Task<BatchUndoOutcome> UndoBatchAsync(
        Guid batchOperationId,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        // The apply rows of this batch are the only undoable rows carrying the
        // batch id; the undo rows written below get a fresh batch id and link
        // back through change_reason instead.
        var batchRows = await db.CalendarEntryHistory
            .AsNoTracking()
            .Where(row => row.BatchOperationId == batchOperationId)
            .OrderBy(row => row.MealDate)
            .ThenBy(row => row.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var applyRows = batchRows
            .Where(row => IsBatchApplyMetadata(row.MetadataJson))
            .ToArray();
        if (applyRows.Length == 0)
        {
            return BatchUndoExpired();
        }

        // A prior undo of this batch wrote rows whose change_reason names the
        // original batch id, so a second call is safely reported as expired.
        var undoReason = BatchUndoChangeReason(batchOperationId);
        var alreadyUndone = await db.CalendarEntryHistory
            .AsNoTracking()
            .AnyAsync(row => row.ChangeReason == undoReason, cancellationToken)
            .ConfigureAwait(false);
        if (alreadyUndone)
        {
            return BatchUndoExpired();
        }

        // One transaction owns every lock, restore, and history write; any early
        // return disposes it and rolls the whole undo back.
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Serialize per affected date with the same advisory lock as M6/M7 apply,
        // acquired in ascending date order (docs/M7_PLAN.md §1.12).
        foreach (var row in applyRows)
        {
            await LockDateAsync(row.MealDate, cancellationToken).ConfigureAwait(false);
        }

        // Every apply row must still be the latest change for its meal date; a
        // later lock/upsert/delete/reroll makes the whole batch undo ambiguous.
        foreach (var row in applyRows)
        {
            var latest = await db.CalendarEntryHistory
                .AsNoTracking()
                .Where(candidate => candidate.MealDate == row.MealDate)
                .OrderByDescending(candidate => candidate.ChangedAt)
                .ThenByDescending(candidate => candidate.Id)
                .FirstAsync(cancellationToken)
                .ConfigureAwait(false);
            if (latest.Id != row.Id)
            {
                return BatchUndoConflict();
            }
        }

        // Past dates are immutable in normal use (AGENTS.md): a batch undo may
        // not rewrite a date that has since become historical.
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (applyRows.Any(row => HouseholdClock.IsHistorical(row.MealDate, context)))
        {
            return BatchUndoPastDate();
        }

        var now = timeProvider.GetUtcNow();
        var undoBatchOperationId = Guid.NewGuid();
        var undoMetadataJson = SerializeBatchUndoMetadata(batchOperationId);

        foreach (var row in applyRows)
        {
            var entry = await db.CalendarEntries
                .FirstOrDefaultAsync(candidate => candidate.MealDate == row.MealDate, cancellationToken)
                .ConfigureAwait(false);

            var undoBeforeJson = entry is null ? null : CalendarService.SnapshotJson(entry);

            long? restoredEntryId = null;
            string? restoredAfterJson = null;
            if (row.BeforeJson is null)
            {
                // The date was blank before the apply: restoring means deleting
                // the entry the apply created.
                if (entry is not null)
                {
                    db.CalendarEntries.Remove(entry);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                if (entry is null)
                {
                    // Defensive: the apply's snapshot proves an entry existed.
                    entry = new CalendarEntry
                    {
                        MealDate = row.MealDate,
                        SnapshotDisplayText = string.Empty,
                        CreatedAt = now,
                        CreatedByUserId = actorUserId,
                    };
                    db.CalendarEntries.Add(entry);
                }

                CalendarRandomizer.ApplySnapshot(entry, row.BeforeJson);
                entry.UpdatedAt = now;
                entry.UpdatedByUserId = actorUserId;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                restoredEntryId = entry.Id;
                restoredAfterJson = CalendarService.SnapshotJson(entry);
            }

            db.CalendarEntryHistory.Add(new CalendarEntryHistory
            {
                CalendarEntryId = restoredEntryId,
                MealDate = row.MealDate,
                OperationId = Guid.NewGuid(),
                BatchOperationId = undoBatchOperationId,
                BeforeJson = undoBeforeJson,
                AfterJson = restoredAfterJson,
                MetadataJson = undoMetadataJson,
                ChangedByUserId = actorUserId,
                ChangeReason = undoReason,
                ChangedAt = now,
                IsHistoricalCorrection = false,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var days = new List<CalendarDayRecord>(applyRows.Length);
        foreach (var row in applyRows)
        {
            days.Add(await calendar.GetDayAsync(row.MealDate, cancellationToken).ConfigureAwait(false));
        }

        return new BatchUndoOutcome.Success(days);
    }

    /// <summary>
    /// Reads the stored proposal from <c>proposal_json</c>; null when the JSON is
    /// malformed or lacks the expected day list. Metadata is kept as its raw JSON
    /// text so the exact per-date breakdown is written to the history row.
    /// </summary>
    private static StoredProposal? ReadProposal(string proposalJson)
    {
        try
        {
            using var document = JsonDocument.Parse(proposalJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("days", out var daysElement)
                || daysElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var days = new List<StoredProposalDay>(daysElement.GetArrayLength());
            foreach (var dayElement in daysElement.EnumerateArray())
            {
                if (!TryReadDate(dayElement, "date", out var date))
                {
                    return null;
                }

                var metadataJson = dayElement.TryGetProperty("metadata", out var metadata)
                    && metadata.ValueKind == JsonValueKind.Object
                    ? metadata.GetRawText()
                    : null;

                days.Add(new StoredProposalDay(
                    date,
                    ReadString(dayElement, "action"),
                    ReadLong(dayElement, "recipe_id"),
                    ReadLong(dayElement, "before_entry_id"),
                    ReadDateTimeOffset(dayElement, "before_updated_at"),
                    metadataJson));
            }

            return new StoredProposal(days);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the stored before-state still matches the date's current entry
    /// (a blank date has no entry; an existing one keeps its id and updated_at).
    /// </summary>
    private static bool MatchesBeforeState(CalendarEntry? current, StoredProposalDay day)
    {
        if (day.BeforeEntryId is null)
        {
            return current is null;
        }

        return current is not null
            && current.Id == day.BeforeEntryId
            && current.UpdatedAt == day.BeforeUpdatedAt;
    }

    /// <summary>
    /// Takes the per-date transaction-scoped advisory lock so concurrent apply
    /// and single-date mutations of the same date serialize (docs/M7_PLAN.md
    /// §1.12). The lock is released on commit/rollback.
    /// </summary>
    private Task LockDateAsync(DateOnly date, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { (long)date.DayNumber },
            cancellationToken);

    private static bool TryReadDate(JsonElement element, string field, out DateOnly date)
    {
        if (element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(
                value.GetString(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
        {
            return true;
        }

        date = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var parsed)
            ? parsed
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string field) =>
        element.TryGetProperty(field, out var value)
            && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
            ? parsed
            : null;

    private static BatchApplyOutcome.Failure DraftExpired() =>
        new(
            StatusCodes.Status410Gone,
            ErrorCodes.BatchDraftExpired,
            "This batch preview is missing, expired, or has already been applied.");

    private static BatchApplyOutcome.Failure Conflict() =>
        new(
            StatusCodes.Status409Conflict,
            ErrorCodes.BatchConflict,
            "A date in this batch changed since the preview. Preview again and re-apply.");

    /// <summary>True when a history row's metadata is a batch apply document.</summary>
    private static bool IsBatchApplyMetadata(string? metadataJson)
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
                && kind.GetString() is "batch_reroll" or "fill_empty";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string BatchUndoChangeReason(Guid batchOperationId) =>
        $"undo:{batchOperationId:D}";

    /// <summary>
    /// Serializes batch undo metadata in the documented snake_case shape:
    /// <c>{"kind":"undo","undone_batch_operation_id":"..."}</c>.
    /// </summary>
    private static string SerializeBatchUndoMetadata(Guid batchOperationId) =>
        JsonSerializer.Serialize(
            new BatchUndoMetadataDocument("undo", batchOperationId),
            ProposalJsonOptions);

    private static BatchUndoOutcome.Failure BatchUndoExpired() =>
        new(
            StatusCodes.Status410Gone,
            ErrorCodes.UndoExpired,
            "This batch operation is missing or has already been undone.");

    private static BatchUndoOutcome.Failure BatchUndoConflict() =>
        new(
            StatusCodes.Status409Conflict,
            ErrorCodes.UndoConflict,
            "A date in this batch changed since it was applied. Undo is no longer available.");

    private static BatchUndoOutcome.Failure BatchUndoPastDate() =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.CalendarDateInPast,
            "Dates in the past are read-only. Use the admin historical correction workflow.");

    /// <summary>Snake_case serialization shape for a batch undo history row.</summary>
    private sealed record BatchUndoMetadataDocument(string Kind, Guid UndoneBatchOperationId);

    /// <summary>One day of a stored proposal, read from <c>proposal_json</c>.</summary>
    private sealed record StoredProposalDay(
        DateOnly Date,
        string? Action,
        long? RecipeId,
        long? BeforeEntryId,
        DateTimeOffset? BeforeUpdatedAt,
        string? MetadataJson);

    /// <summary>The stored proposal document read by Apply.</summary>
    private sealed record StoredProposal(IReadOnlyList<StoredProposalDay> Days);

    private static DateOnly ResolveEndDate(DateOnly startDate, string mode) => mode switch
    {
        ModeSevenDays => startDate.AddDays(6),
        _ => new DateOnly(
            startDate.Year,
            startDate.Month,
            DateTime.DaysInMonth(startDate.Year, startDate.Month)),
    };

    private static bool WithinHorizon(DateOnly date, DateOnly today)
    {
        var earliest = today.AddYears(-HouseholdClock.HorizonYears);
        var latest = today.AddYears(HouseholdClock.HorizonYears);
        return date >= earliest && date <= latest;
    }

    private static DayDecision Decide(CalendarEntryRecord? current, string mode)
    {
        if (current is null)
        {
            return new DayDecision(Propose: true, Reason: null);
        }

        // Fill Empty never alters an existing entry (docs/RANDOMIZER.md §12).
        if (mode == ModeFillEmpty)
        {
            return new DayDecision(Propose: false, Reason: UnchangedReason(current));
        }

        if (current.IsLocked)
        {
            return new DayDecision(Propose: false, Reason: ReasonLocked);
        }

        if (!EnumTextConverters.TryCalendarEntryTypeFromText(current.EntryType, out var type))
        {
            return new DayDecision(Propose: false, Reason: ReasonExistingNonRecipe);
        }

        return type switch
        {
            CalendarEntryType.Vacation => new DayDecision(false, ReasonVacation),
            CalendarEntryType.Holiday or CalendarEntryType.HostFamilyDinner
                or CalendarEntryType.Misc or CalendarEntryType.GoToFamilyDinner =>
                new DayDecision(false, ReasonSpecial),
            CalendarEntryType.EatOut or CalendarEntryType.FendForYourself =>
                new DayDecision(false, ReasonExistingNonRecipe),
            CalendarEntryType.Recipe => new DayDecision(true, null),
            _ => new DayDecision(false, ReasonExistingNonRecipe),
        };
    }

    /// <summary>The reason an existing entry is preserved, or null when no reason applies.</summary>
    private static string? UnchangedReason(CalendarEntryRecord current)
    {
        if (current.IsLocked)
        {
            return ReasonLocked;
        }

        if (!EnumTextConverters.TryCalendarEntryTypeFromText(current.EntryType, out var type))
        {
            return ReasonExistingNonRecipe;
        }

        return type switch
        {
            CalendarEntryType.Vacation => ReasonVacation,
            CalendarEntryType.Holiday or CalendarEntryType.HostFamilyDinner
                or CalendarEntryType.Misc or CalendarEntryType.GoToFamilyDinner => ReasonSpecial,
            CalendarEntryType.EatOut or CalendarEntryType.FendForYourself => ReasonExistingNonRecipe,
            // Fill Empty leaves an existing plain meal alone; no blocking reason applies.
            CalendarEntryType.Recipe => null,
            _ => ReasonExistingNonRecipe,
        };
    }

    private static List<RandomizerOccurrence> BuildOccurrences(
        IReadOnlyList<CalendarEntry> contextEntries,
        IReadOnlyDictionary<DateOnly, long> proposedByDate,
        IReadOnlySet<DateOnly> replacedDates,
        DateOnly date,
        int radius)
    {
        var occurrences = new List<RandomizerOccurrence>();
        foreach (var entry in contextEntries)
        {
            if (entry.RecipeId is null
                || entry.MealDate == date
                || replacedDates.Contains(entry.MealDate)
                || Math.Abs(entry.MealDate.DayNumber - date.DayNumber) > radius)
            {
                continue;
            }

            occurrences.Add(new RandomizerOccurrence(
                entry.RecipeId.Value,
                entry.MealDate,
                IsSpecialDay(entry.EntryType)));
        }

        // Recipes chosen earlier in this same preview count as occurrences at
        // their proposed dates (docs/RANDOMIZER.md §9, docs/M7_PLAN.md §1.6).
        foreach (var (proposedDate, recipeId) in proposedByDate)
        {
            if (Math.Abs(proposedDate.DayNumber - date.DayNumber) > radius)
            {
                continue;
            }

            occurrences.Add(new RandomizerOccurrence(recipeId, proposedDate, IsSpecialDay: false));
        }

        return occurrences;
    }

    private static List<RandomizerNeighbor> BuildNeighbors(
        IReadOnlyList<CalendarEntry> contextEntries,
        IReadOnlyDictionary<DateOnly, long> proposedByDate,
        IReadOnlySet<DateOnly> replacedDates,
        IReadOnlyDictionary<long, HashSet<string>> proteinKeys,
        DateOnly date,
        int radius)
    {
        var neighbors = new List<RandomizerNeighbor>();
        foreach (var entry in contextEntries)
        {
            if (entry.RecipeId is null
                || entry.MealDate == date
                || replacedDates.Contains(entry.MealDate)
                || Math.Abs(entry.MealDate.DayNumber - date.DayNumber) > radius)
            {
                continue;
            }

            neighbors.Add(new RandomizerNeighbor(
                entry.MealDate,
                ProteinKeys(proteinKeys, entry.RecipeId.Value)));
        }

        foreach (var (proposedDate, recipeId) in proposedByDate)
        {
            if (Math.Abs(proposedDate.DayNumber - date.DayNumber) > radius)
            {
                continue;
            }

            neighbors.Add(new RandomizerNeighbor(proposedDate, ProteinKeys(proteinKeys, recipeId)));
        }

        return neighbors;
    }

    private static bool IsSpecialDay(CalendarEntryType type) =>
        type is CalendarEntryType.Holiday
            or CalendarEntryType.HostFamilyDinner
            or CalendarEntryType.Misc;

    private static IReadOnlySet<string> ProteinKeys(
        IReadOnlyDictionary<long, HashSet<string>> proteinKeys,
        long recipeId) =>
        proteinKeys.TryGetValue(recipeId, out var keys) ? keys : EmptyProteinKeys;

    private async Task<IReadOnlyDictionary<long, HashSet<string>>> LoadProteinKeysAsync(
        long[] recipeIds,
        CancellationToken cancellationToken)
    {
        if (recipeIds.Length == 0)
        {
            return new Dictionary<long, HashSet<string>>();
        }

        var rows = await db.RecipeProteins
            .AsNoTracking()
            .Where(link => recipeIds.Contains(link.RecipeId))
            .Join(
                db.Proteins.AsNoTracking(),
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

    private static BatchPreviewOutcome.Failure PastDate() =>
        new(
            StatusCodes.Status422UnprocessableEntity,
            ErrorCodes.CalendarDateInPast,
            "Dates in the past are read-only. Use the admin historical correction workflow.");

    /// <summary>One day's preview action.</summary>
    private sealed record DayDecision(bool Propose, string? Reason);

    /// <summary>The persisted <c>proposal_json</c> document (docs/M7_PLAN.md §2.3).</summary>
    private sealed record ProposalDocument(
        string Mode,
        DateOnly StartDate,
        DateOnly EndDate,
        IReadOnlyList<ProposalDayDocument> Days);

    /// <summary>One day in <see cref="ProposalDocument"/>.</summary>
    private sealed record ProposalDayDocument(
        DateOnly Date,
        string Action,
        string? Reason,
        long? RecipeId,
        long? BeforeEntryId,
        DateTimeOffset? BeforeUpdatedAt,
        ProposalMetadataDocument? Metadata)
    {
        public static ProposalDayDocument Unchanged(
            DateOnly date,
            string? reason,
            CalendarEntryRecord? current) =>
            new(date, "unchanged", reason, null, current?.Id, current?.UpdatedAt, null);

        public static ProposalDayDocument Proposed(
            DateOnly date,
            long recipeId,
            CalendarEntryRecord? current,
            RandomizerMetadata metadata,
            string mode) =>
            new(
                date,
                ActionPropose,
                null,
                recipeId,
                current?.Id,
                current?.UpdatedAt,
                ProposalMetadataDocument.From(metadata, mode));
    }

    /// <summary>The per-date score breakdown, shaped for the apply history row.</summary>
    private sealed record ProposalMetadataDocument(
        string Kind,
        string Mode,
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
        decimal FinalWeight)
    {
        public static ProposalMetadataDocument From(RandomizerMetadata metadata, string mode) =>
            new(
                mode == ModeFillEmpty ? "fill_empty" : "batch_reroll",
                mode,
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
                metadata.FinalWeight);
    }
}
