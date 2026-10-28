using System.Text;

using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// One active unit plus its aliases, as read from reference data for import
/// matching. Deliberately independent of EF Core so the
/// normalizer is testable without a database.
/// </summary>
/// <param name="Id">Unit primary key.</param>
/// <param name="Symbol">Canonical short symbol (for example <c>tbsp</c>).</param>
/// <param name="DisplayName">Canonical display name (for example <c>tablespoon</c>).</param>
/// <param name="Dimension">Mass, volume, count, or nonconvertible.</param>
/// <param name="Aliases">Additional accepted spellings seeded for the unit.</param>
public sealed record ImportUnitCandidate(
    long Id,
    string Symbol,
    string DisplayName,
    UnitDimension Dimension,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Folded unit-text helpers shared by <see cref="IngredientLineParser"/> and
/// <see cref="ImportNormalizer"/>. Folding uses
/// <see cref="ReferenceDataNormalizer"/> (never <c>String.Normalize</c>) and
/// additionally removes periods so <c>fl. oz.</c> and <c>Tbsp.</c> match their
/// seeded symbols.
/// </summary>
public static class ImportUnitText
{
    /// <summary>
    /// Common cooking unit words that are intentionally not part of the M2 seed
    /// but whose dimension is unambiguous. Used only as a conservative fallback
    /// when no known unit phrase matches, both to split the unit token and to
    /// suggest a dimension for the review screen.
    /// </summary>
    private static readonly Dictionary<string, UnitDimension> DimensionHints = BuildDimensionHints();

    /// <summary>Folds unit text to the lookup form: lowercase, diacritics folded, periods removed.</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = ReferenceDataNormalizer.Normalize(value) is ReferenceDataNormalizationResult.Success success
            ? success.Normalized
            : value.Trim().ToLowerInvariant();

        var builder = new StringBuilder(normalized.Length);
        var previousWasWhitespace = false;
        foreach (var character in normalized)
        {
            if (character == '.')
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    /// <summary>True when the folded text names a known dimension hint.</summary>
    public static bool TryGetDimension(string foldedText, out UnitDimension dimension)
    {
        if (!string.IsNullOrEmpty(foldedText) && DimensionHints.TryGetValue(foldedText, out dimension))
        {
            return true;
        }

        dimension = default;
        return false;
    }

    /// <summary>
    /// Case-preserving normalization for exact unit matching: trim, collapse
    /// whitespace, and remove periods. The letter case is kept so <c>T</c>
    /// (tablespoon) and <c>t</c> (teaspoon) remain distinct, as
    /// docs/PRODUCT_SPEC.md §36 intends.
    /// </summary>
    public static string ExactKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value.Trim())
        {
            if (character == '.')
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }

    private static Dictionary<string, UnitDimension> BuildDimensionHints()
    {
        var hints = new Dictionary<string, UnitDimension>(StringComparer.Ordinal);
        Add(hints, UnitDimension.Volume, "ml", "milliliter", "milliliters", "millilitre", "millilitres",
            "l", "liter", "liters", "litre", "litres", "dl", "cl", "cc");
        Add(hints, UnitDimension.Mass, "mg", "milligram", "milligrams", "mcg", "microgram", "micrograms",
            "ug", "tonne", "tonnes", "ton", "tons", "g", "gram", "grams", "kg", "kilogram", "kilograms");
        Add(hints, UnitDimension.Count, "dozen", "dozens", "dz", "piece", "pieces", "pc", "pcs",
            "stalk", "stalks", "sprig", "sprigs", "head", "heads", "handful", "handfuls", "dash", "dashes",
            "drop", "drops", "ear", "ears", "rib", "ribs", "knob", "knobs", "punnet", "punnets",
            "packet", "packets", "sachet", "sachets", "tub", "tubs", "box", "boxes", "bag", "bags",
            "carton", "cartons", "sheet", "sheets", "leaf", "leaves");
        return hints;
    }

    private static void Add(Dictionary<string, UnitDimension> hints, UnitDimension dimension, params string[] terms)
    {
        foreach (var term in terms)
        {
            hints[term] = dimension;
        }
    }
}

/// <summary>
/// Deterministic quantity clamping and unit resolution for imported ingredients
/// Reads no database; a catalog of active units
/// is supplied by the caller.
/// </summary>
public sealed class ImportNormalizer
{
    /// <summary>Maximum unit candidates returned for an unknown unit.</summary>
    public const int MaxSuggestions = 5;

    private readonly IReadOnlyList<ImportUnitCandidate> _units;
    private readonly IReadOnlyList<string> _knownPhrases;
    private readonly Dictionary<long, ImportUnitCandidate> _byId;
    private readonly Dictionary<string, HashSet<long>> _exactUnits;
    private readonly Dictionary<string, HashSet<long>> _foldedUnits;

    /// <summary>
    /// Builds the case-sensitive and folded lookups from the supplied active
    /// units and their aliases. A folded key shared by more than one unit is
    /// recorded as ambiguous and resolved only by an exact-case match.
    /// </summary>
    public ImportNormalizer(IReadOnlyList<ImportUnitCandidate> units)
    {
        _units = units ?? [];
        _byId = new Dictionary<long, ImportUnitCandidate>();
        _exactUnits = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        _foldedUnits = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        var phrases = new List<string>();
        var phraseSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in _units)
        {
            _byId.TryAdd(unit.Id, unit);
            AddPhrase(unit.Symbol, unit, phrases, phraseSet);
            AddPhrase(unit.DisplayName, unit, phrases, phraseSet);
            foreach (var alias in unit.Aliases)
            {
                AddPhrase(alias, unit, phrases, phraseSet);
            }
        }

        _knownPhrases = phrases;
    }

    /// <summary>Known folded unit phrases, for <see cref="IngredientLineParser"/>.</summary>
    public IReadOnlyList<string> KnownUnitPhrases => _knownPhrases;

    /// <summary>Parses one raw line using this normalizer's known unit phrases.</summary>
    public ParsedIngredientLine Parse(string? rawLine) =>
        IngredientLineParser.Parse(rawLine, _knownPhrases);

    /// <summary>
    /// Resolves raw unit text against the catalog. An exact, case-sensitive
    /// match is tried first (so <c>T</c> → tablespoon and <c>t</c> → teaspoon)
    /// a folded key shared by multiple units stays <c>is_unknown = true</c>
    /// with the colliding units as suggestions rather than guessing. Unknown
    /// text reports a suggested dimension when obvious and up to
    /// <see cref="MaxSuggestions"/> near matches.
    /// </summary>
    public ImportDraftUnit ResolveUnit(string? rawUnit)
    {
        if (string.IsNullOrWhiteSpace(rawUnit))
        {
            return new ImportDraftUnit(null, null, null, true, null, []);
        }

        var raw = rawUnit.Trim();

        var exact = ImportUnitText.ExactKey(raw);
        if (exact.Length > 0 && _exactUnits.TryGetValue(exact, out var exactIds))
        {
            return exactIds.Count == 1
                ? Matched(raw, _byId[First(exactIds)])
                : Ambiguous(raw, exactIds);
        }

        var folded = ImportUnitText.Fold(raw);
        if (folded.Length > 0 && _foldedUnits.TryGetValue(folded, out var foldedIds))
        {
            return foldedIds.Count == 1
                ? Matched(raw, _byId[First(foldedIds)])
                : Ambiguous(raw, foldedIds);
        }

        var dimension = ImportUnitText.TryGetDimension(folded, out var hinted) ? DimensionName(hinted) : null;
        return new ImportDraftUnit(raw, null, null, true, dimension, Suggest(folded));
    }

    private static ImportDraftUnit Matched(string raw, ImportUnitCandidate unit) =>
        new(
            raw,
            unit.Id,
            unit.Symbol,
            false,
            null,
            [new ImportDraftUnitSuggestion(unit.Id, unit.Symbol, unit.DisplayName)]);

    /// <summary>A folded key shared by several units: never guess, offer them.</summary>
    private ImportDraftUnit Ambiguous(string raw, HashSet<long> ids)
    {
        var dimension = ImportUnitText.TryGetDimension(ImportUnitText.Fold(raw), out var hinted)
            ? DimensionName(hinted)
            : null;
        var suggestions = ids
            .Select(id => _byId[id])
            .OrderBy(unit => unit.Symbol, StringComparer.Ordinal)
            .ThenBy(unit => unit.Id)
            .Take(MaxSuggestions)
            .Select(unit => new ImportDraftUnitSuggestion(unit.Id, unit.Symbol, unit.DisplayName))
            .ToArray();
        return new ImportDraftUnit(raw, null, null, true, dimension, suggestions);
    }

    private static long First(HashSet<long> ids)
    {
        foreach (var id in ids)
        {
            return id;
        }

        throw new InvalidOperationException("A unit lookup set must not be empty.");
    }

    /// <summary>
    /// Clamps a parsed quantity to the M3 bounds (<c>0 ≤ q ≤ 1,000,000</c>
    /// at most <see cref="RecipeService.MaxQuantityScale"/> decimal places).
    /// </summary>
    public static decimal? ClampQuantity(decimal? quantity)
    {
        if (quantity is not { } value)
        {
            return null;
        }

        if (value < 0m)
        {
            value = 0m;
        }
        else if (value > RecipeService.MaxQuantity)
        {
            value = RecipeService.MaxQuantity;
        }

        return decimal.Round(value, RecipeService.MaxQuantityScale);
    }

    private static string? DimensionName(UnitDimension dimension) => dimension switch
    {
        UnitDimension.Mass => "mass",
        UnitDimension.Volume => "volume",
        UnitDimension.Count => "count",
        _ => null,
    };

    private IReadOnlyList<ImportDraftUnitSuggestion> Suggest(string folded)
    {
        if (folded.Length == 0)
        {
            return [];
        }

        List<(int Rank, ImportUnitCandidate Unit)> ranked = [];
        foreach (var unit in _units)
        {
            var rank = Rank(unit, folded);
            if (rank < int.MaxValue)
            {
                ranked.Add((rank, unit));
            }
        }

        return ranked
            .OrderBy(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Unit.Symbol, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Unit.Id)
            .Take(MaxSuggestions)
            .Select(candidate => new ImportDraftUnitSuggestion(
                candidate.Unit.Id,
                candidate.Unit.Symbol,
                candidate.Unit.DisplayName))
            .ToArray();
    }

    private static int Rank(ImportUnitCandidate unit, string folded)
    {
        var best = int.MaxValue;
        best = Math.Min(best, RankPhrase(ImportUnitText.Fold(unit.Symbol), folded));
        best = Math.Min(best, RankPhrase(ImportUnitText.Fold(unit.DisplayName), folded));
        foreach (var alias in unit.Aliases)
        {
            best = Math.Min(best, RankPhrase(ImportUnitText.Fold(alias), folded));
        }

        return best;
    }

    private static int RankPhrase(string phrase, string folded)
    {
        if (phrase.Length == 0)
        {
            return int.MaxValue;
        }

        if (phrase.StartsWith(folded, StringComparison.Ordinal))
        {
            return 1;
        }

        if (folded.StartsWith(phrase, StringComparison.Ordinal))
        {
            return 2;
        }

        return phrase.Contains(folded, StringComparison.Ordinal) ? 3 : int.MaxValue;
    }

    private void AddPhrase(
        string? phrase,
        ImportUnitCandidate unit,
        List<string> phrases,
        HashSet<string> phraseSet)
    {
        var folded = ImportUnitText.Fold(phrase);
        if (folded.Length == 0)
        {
            return;
        }

        if (phraseSet.Add(folded))
        {
            phrases.Add(folded);
        }

        AddLookup(_foldedUnits, folded, unit.Id);

        var exact = ImportUnitText.ExactKey(phrase);
        if (exact.Length > 0)
        {
            AddLookup(_exactUnits, exact, unit.Id);
        }
    }

    private static void AddLookup(Dictionary<string, HashSet<long>> lookup, string key, long unitId)
    {
        if (!lookup.TryGetValue(key, out var ids))
        {
            ids = [];
            lookup[key] = ids;
        }

        ids.Add(unitId);
    }
}
