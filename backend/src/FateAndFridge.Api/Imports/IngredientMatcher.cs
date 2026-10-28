using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// One active canonical ingredient plus its aliases, as read from reference
/// data for import matching. Deliberately independent of
/// EF Core so the matcher is testable without a database.
/// </summary>
/// <param name="Id">Ingredient primary key.</param>
/// <param name="CanonicalName">Canonical display name (for example <c>Scallion</c>).</param>
/// <param name="IsActive">
/// Active/retired state (docs/PRODUCT_SPEC.md §31). Retired and merged
/// ingredients are excluded from matches and suggestions even when a caller
/// passes them in.
/// </param>
/// <param name="Aliases">Additional accepted spellings (docs/PRODUCT_SPEC.md §32).</param>
public sealed record IngredientCandidate(
    long Id,
    string CanonicalName,
    bool IsActive,
    IReadOnlyList<string> Aliases);

/// <summary>
/// One ingredient line's canonical match. A
/// confident match sets <see cref="MatchedIngredientId"/> and
/// <see cref="MatchKind"/>; otherwise <see cref="IsUnknown"/> is true and the
/// review screen resolves it against <see cref="Suggestions"/>.
/// </summary>
/// <param name="MatchedIngredientId">Canonical ingredient id, or null when unknown.</param>
/// <param name="MatchKind"><c>exact</c>, <c>alias</c>, or null when unknown.</param>
/// <param name="IsUnknown">True when no confident match exists.</param>
/// <param name="Suggestions">Bounded nearest candidates (max 5), never retired.</param>
public sealed record IngredientMatchResult(
    long? MatchedIngredientId,
    string? MatchKind,
    bool IsUnknown,
    IReadOnlyList<ImportDraftReference> Suggestions);

/// <summary>
/// Deterministic canonical/alias ingredient matching for imported lines
/// It reads no database; a catalog of active
/// ingredients is supplied by the caller once per import. Matching is
/// <b>read-only</b> and never throws on adversarial names.
/// </summary>
/// <remarks>
/// <para>
/// Folding uses <see cref="ReferenceDataNormalizer"/> (trim, whitespace
/// collapse, table-driven diacritic folding, invariant lowercase) — never
/// <c>String.Normalize</c>, which is a silent no-op under this workstation's
/// invariant globalization.
/// </para>
/// <para>
/// Order of confidence: folded canonical name exact, then folded alias exact.
/// A folded key shared by more than one active ingredient is ambiguous and is
/// never guessed. Retired/merged ingredients (<c>IsActive = false</c>) are
/// excluded from both matches and suggestions (docs/PRODUCT_SPEC.md §31).
/// </para>
/// </remarks>
public sealed class IngredientMatcher
{
    /// <summary>Maximum suggestions returned for an unmatched line.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>Match-kind value for an exact canonical-name match.</summary>
    public const string ExactKind = "exact";

    /// <summary>Match-kind value for an exact alias match.</summary>
    public const string AliasKind = "alias";

    private readonly IReadOnlyList<IngredientCandidate> _active;
    private readonly Dictionary<long, IngredientCandidate> _byId;
    private readonly Dictionary<string, List<long>> _byCanonical;
    private readonly Dictionary<string, List<long>> _byAlias;

    /// <summary>
    /// Builds folded canonical-name and alias lookups from the supplied
    /// catalog. Candidates are de-duplicated by id and inactive ones are
    /// discarded, so a caller may pass the full table without filtering.
    /// </summary>
    public IngredientMatcher(IReadOnlyList<IngredientCandidate> catalog)
    {
        _byId = new Dictionary<long, IngredientCandidate>();
        _byCanonical = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        _byAlias = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        var active = new List<IngredientCandidate>();
        if (catalog is not null)
        {
            foreach (var candidate in catalog)
            {
                if (candidate is null || !candidate.IsActive || !_byId.TryAdd(candidate.Id, candidate))
                {
                    continue;
                }

                active.Add(candidate);
                AddLookup(_byCanonical, Fold(candidate.CanonicalName), candidate.Id);
                foreach (var alias in candidate.Aliases ?? [])
                {
                    AddLookup(_byAlias, Fold(alias), candidate.Id);
                }
            }
        }

        _active = active;
    }

    /// <summary>Number of active ingredients in the catalog.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>
    /// Matches one imported ingredient name. Returns an
    /// unknown result with bounded suggestions when no confident match exists
    /// and never throws.
    /// </summary>
    public IngredientMatchResult Match(string? rawName)
    {
        var folded = Fold(rawName);
        if (folded.Length == 0)
        {
            return Unknown([]);
        }

        // Canonical exact first, then alias exact. A folded key shared by more
        // than one active ingredient is ambiguous: do not guess.
        if (_byCanonical.TryGetValue(folded, out var canonicalIds) && canonicalIds.Count == 1)
        {
            return Matched(canonicalIds[0], ExactKind);
        }

        if (_byAlias.TryGetValue(folded, out var aliasIds) && aliasIds.Count == 1)
        {
            return Matched(aliasIds[0], AliasKind);
        }

        return Unknown(Suggest(folded));
    }

    private IngredientMatchResult Matched(long id, string kind) =>
        new(id, kind, false, [new ImportDraftReference(id, _byId[id].CanonicalName)]);

    private static IngredientMatchResult Unknown(IReadOnlyList<ImportDraftReference> suggestions) =>
        new(null, null, true, suggestions);

    /// <summary>
    /// Bounded prefix/substring suggestions over active canonical names and
    /// aliases. Ordering is deterministic: exact-prefix before substring, then
    /// shorter phrase, then lowest ingredient id. Never includes retired data.
    /// </summary>
    private IReadOnlyList<ImportDraftReference> Suggest(string folded)
    {
        List<(int Rank, int Length, long Id)> ranked = [];
        foreach (var candidate in _active)
        {
            var best = int.MaxValue;
            var bestLength = int.MaxValue;

            Consider(candidate.CanonicalName, candidate.Id, ref best, ref bestLength);
            foreach (var alias in candidate.Aliases ?? [])
            {
                Consider(alias, candidate.Id, ref best, ref bestLength);
            }

            if (best < int.MaxValue)
            {
                ranked.Add((best, bestLength, candidate.Id));
            }
        }

        return ranked
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Length)
            .ThenBy(match => match.Id)
            .Take(MaxSuggestions)
            .Select(match => new ImportDraftReference(match.Id, _byId[match.Id].CanonicalName))
            .ToArray();

        void Consider(string? phrase, long id, ref int best, ref int bestLength)
        {
            var candidate = Fold(phrase);
            if (candidate.Length == 0)
            {
                return;
            }

            int rank;
            if (candidate.StartsWith(folded, StringComparison.Ordinal)
                || folded.StartsWith(candidate, StringComparison.Ordinal))
            {
                // Exact-prefix in either direction: the candidate starts the
                // input ("flour" → "flour tortillas") or the input starts the
                // candidate ("flo" → "flour").
                rank = 1;
            }
            else if (candidate.Contains(folded, StringComparison.Ordinal)
                || folded.Contains(candidate, StringComparison.Ordinal))
            {
                rank = 2;
            }
            else
            {
                return;
            }

            if (rank < best || (rank == best && candidate.Length < bestLength))
            {
                best = rank;
                bestLength = candidate.Length;
            }
        }
    }

    private static void AddLookup(Dictionary<string, List<long>> lookup, string key, long id)
    {
        if (key.Length == 0)
        {
            return;
        }

        if (!lookup.TryGetValue(key, out var ids))
        {
            ids = [];
            lookup[key] = ids;
        }

        if (!ids.Contains(id))
        {
            ids.Add(id);
        }
    }

    /// <summary>
    /// Folds a name or alias to its lookup form with
    /// <see cref="ReferenceDataNormalizer"/>. Invalid (for example
    /// control-character or blank) input folds to the empty string, so it can
    /// never match; adversarial input never throws.
    /// </summary>
    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return ReferenceDataNormalizer.Normalize(value) is ReferenceDataNormalizationResult.Success success
                ? success.Normalized
                : string.Empty;
        }
        catch (Exception)
        {
            // Defensive: a matcher must never throw on adversarial import text.
            return string.Empty;
        }
    }
}
