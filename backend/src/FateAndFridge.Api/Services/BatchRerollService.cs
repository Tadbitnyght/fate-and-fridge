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
                "propose",
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
