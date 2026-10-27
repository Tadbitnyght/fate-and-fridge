namespace FateAndFridge.Api.Services;

/// <summary>
/// One eligible recipe. <see cref="ProteinKeys"/> are unique normalized primary
/// protein names supplied by the caller (unique in the database).
/// </summary>
public sealed record RandomizerCandidate(long RecipeId, bool IsFavorite, IReadOnlySet<string> ProteinKeys);

/// <summary>
/// A planned occurrence of a recipe on a household-local date. Special events
/// (holiday/host-family dinner/misc) carry <see cref="IsSpecialDay"/>.
/// </summary>
public sealed record RandomizerOccurrence(long RecipeId, DateOnly Date, bool IsSpecialDay);

/// <summary>A neighbor day's recipe-bearing protein keys, used for the adjacency penalty.</summary>
public sealed record RandomizerNeighbor(DateOnly Date, IReadOnlySet<string> ProteinKeys);

/// <summary>Everything the pure engine needs for one selection.</summary>
public sealed record RandomizerRequest(
    DateOnly Date,
    IReadOnlyList<RandomizerCandidate> Candidates,
    IReadOnlyList<RandomizerOccurrence> Occurrences,
    IReadOnlyList<RandomizerNeighbor> Neighbors);

/// <summary>
/// The score breakdown for the chosen recipe (docs/M6_PLAN.md §2.3). Every phase
/// multiplier is recorded even when neutral so the operation metadata is
/// reproducible for analysis.
/// </summary>
public sealed record RandomizerMetadata(
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

/// <summary>The selected recipe and its score breakdown.</summary>
public sealed record RandomizerResult(long RecipeId, RandomizerMetadata Metadata);

/// <summary>
/// Pure randomization engine (docs/M6_PLAN.md §1.5–§1.10, docs/RANDOMIZER.md §7).
/// No I/O: the caller supplies the candidate pool, occurrences, and neighbors as
/// household-local dates. Randomness arrives through <see cref="IRandomSource"/>,
/// so a selection is deterministic for a given source value.
/// </summary>
public sealed class RandomizerEngine(IRandomSource randomSource)
{
    /// <summary>Weight of an otherwise-unpenalized candidate.</summary>
    public const decimal BaseWeight = 1.0m;

    private const decimal NeutralMultiplier = 1.0m;

    /// <summary>
    /// Scores every candidate and draws one with a secure weighted roulette wheel.
    /// Throws when the pool is empty. When every final weight is zero (only
    /// possible with a zero special-day multiplier) it falls back to a uniform
    /// draw, so the small-pool case never fails (docs/M6_PLAN.md §1.10).
    /// </summary>
    public RandomizerResult Select(RandomizerOptions options, RandomizerRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "The randomizer requires at least one eligible candidate (RANDOMIZER_NO_CANDIDATES).");
        }

        var scored = new List<(RandomizerCandidate Candidate, RandomizerMetadata Metadata)>(request.Candidates.Count);
        var totalWeight = 0m;
        foreach (var candidate in request.Candidates)
        {
            var metadata = ScoreCandidate(options, request, candidate);
            scored.Add((candidate, metadata));
            totalWeight += metadata.FinalWeight;
        }

        if (totalWeight <= 0m)
        {
            var fallbackIndex = (int)(randomSource.NextUnitInterval() * request.Candidates.Count);
            fallbackIndex = Math.Clamp(fallbackIndex, 0, request.Candidates.Count - 1);
            var fallback = scored[fallbackIndex];
            return new RandomizerResult(fallback.Candidate.RecipeId, fallback.Metadata);
        }

        var target = randomSource.NextUnitInterval() * totalWeight;
        var cumulative = 0m;
        foreach (var entry in scored)
        {
            cumulative += entry.Metadata.FinalWeight;
            if (target < cumulative)
            {
                return new RandomizerResult(entry.Candidate.RecipeId, entry.Metadata);
            }
        }

        // Decimal summation is exact, so a positive total always returns above.
        // This is a defensive fallback for the last positive entry.
        var last = scored[^1];
        return new RandomizerResult(last.Candidate.RecipeId, last.Metadata);
    }

    /// <summary>
    /// Scores a single candidate with no randomness, exposing each phase for
    /// focused tests. The caller must ensure <paramref name="candidate"/> belongs
    /// to <paramref name="request"/> for <c>CandidateCount</c> to be meaningful.
    /// </summary>
    public RandomizerMetadata ScoreCandidate(
        RandomizerOptions options,
        RandomizerRequest request,
        RandomizerCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidate);

        var repeat = ComputeRepeat(options, request, candidate.RecipeId);
        var protein = ComputeProtein(options, request, candidate);
        var spacing = ComputeSpacing(options, request, candidate.RecipeId);
        var favoriteMultiplier = candidate.IsFavorite ? options.FavoriteMultiplier : NeutralMultiplier;

        var finalWeight = BaseWeight
            * repeat.Multiplier
            * protein.Multiplier
            * spacing.Multiplier
            * favoriteMultiplier;

        return new RandomizerMetadata(
            request.Candidates.Count,
            candidate.RecipeId,
            BaseWeight,
            repeat.Multiplier,
            repeat.Count,
            protein.Multiplier,
            protein.SharedProteins,
            spacing.Days,
            spacing.Multiplier,
            favoriteMultiplier,
            finalWeight);
    }

    private static (decimal Multiplier, int Count) ComputeRepeat(
        RandomizerOptions options,
        RandomizerRequest request,
        long recipeId)
    {
        var earliest = request.Date.AddDays(-options.RepeatLookbackDays);
        var latest = request.Date.AddDays(options.RepeatLookaheadDays);

        var count = 0;
        var hasNormal = false;
        var hasSpecial = false;
        foreach (var occurrence in request.Occurrences)
        {
            if (occurrence.RecipeId != recipeId
                || occurrence.Date == request.Date
                || occurrence.Date < earliest
                || occurrence.Date > latest)
            {
                continue;
            }

            count++;
            if (occurrence.IsSpecialDay)
            {
                hasSpecial = true;
            }
            else
            {
                hasNormal = true;
            }
        }

        var multiplier = hasNormal
            ? options.RepeatMultiplier
            : hasSpecial
                ? options.SpecialDayMultiplier
                : NeutralMultiplier;
        return (multiplier, count);
    }

    private static (decimal Multiplier, IReadOnlyList<string> SharedProteins) ComputeProtein(
        RandomizerOptions options,
        RandomizerRequest request,
        RandomizerCandidate candidate)
    {
        var shared = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var neighbor in request.Neighbors)
        {
            if (neighbor.Date == request.Date)
            {
                continue;
            }

            var distance = Math.Abs(neighbor.Date.DayNumber - request.Date.DayNumber);
            if (distance > options.ProteinNeighborDistance)
            {
                continue;
            }

            foreach (var key in candidate.ProteinKeys)
            {
                if (neighbor.ProteinKeys.Contains(key))
                {
                    shared.Add(key);
                }
            }
        }

        IReadOnlyList<string> sharedProteins = shared.ToArray();
        var multiplier = sharedProteins.Count > 0 ? options.ProteinMultiplier : NeutralMultiplier;
        return (multiplier, sharedProteins);
    }

    private static (int? Days, decimal Multiplier) ComputeSpacing(
        RandomizerOptions options,
        RandomizerRequest request,
        long recipeId)
    {
        int? nearest = null;
        foreach (var occurrence in request.Occurrences)
        {
            if (occurrence.RecipeId != recipeId || occurrence.Date == request.Date)
            {
                continue;
            }

            var distance = Math.Abs(occurrence.Date.DayNumber - request.Date.DayNumber);
            if (distance > options.SpacingSearchDays)
            {
                continue;
            }

            if (nearest is null || distance < nearest.Value)
            {
                nearest = distance;
            }
        }

        if (nearest is null)
        {
            return (null, NeutralMultiplier);
        }

        var days = nearest.Value;
        var multiplier = days <= options.SpacingNearDays
            ? RandomizerOptions.SpacingNearFactor
            : days >= options.SpacingFarDays
                ? RandomizerOptions.SpacingFarFactor
                : RandomizerOptions.SpacingMiddleFactor;
        return (days, multiplier);
    }
}
