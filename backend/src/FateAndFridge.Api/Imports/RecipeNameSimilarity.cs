namespace FateAndFridge.Api.Imports;

/// <summary>
/// Deterministic similar-name scoring for the import duplicate warning
/// It recognizes, in descending confidence, folded
/// exact equality, whole-word containment, shared tokens, and a small edit
/// distance between two already-folded normalized names.
/// </summary>
/// <remarks>
/// <para>
/// The type is pure and dependency-free: it reads no database and never calls
/// <c>String.Normalize</c>, so it behaves identically under globalization
/// invariant mode. Callers pass the canonical folded names produced by
/// <c>ReferenceDataNormalizer</c>; the scorer tokenizes on non-alphanumeric
/// characters so punctuation (for example the apostrophe in <c>grandma's</c>)
/// never joins two words.
/// </para>
/// <para>
/// Scoring is intentionally coarse and only ranks candidates: the caller keeps
/// the highest scores. Word-boundary containment (not raw substring) avoids
/// false positives such as <c>lasagna</c> matching <c>lasagnabake</c>, and a
/// warning that is missed is always preferred over one that is wrong.
/// </para>
/// </remarks>
public static class RecipeNameSimilarity
{
    /// <summary>Match kind for folded exact equality.</summary>
    public const string ExactKind = "exact";

    /// <summary>Match kind for whole-word containment in either direction.</summary>
    public const string ContainmentKind = "containment";

    /// <summary>Match kind for one or more shared whole tokens.</summary>
    public const string TokenOverlapKind = "token_overlap";

    /// <summary>Match kind for a small Levenshtein distance on the folded forms.</summary>
    public const string EditDistanceKind = "edit_distance";

    /// <summary>Largest Levenshtein distance still treated as similar.</summary>
    public const int MaxEditDistance = 2;

    private const int ExactScore = 1_000;
    private const int ContainmentBaseScore = 600;
    private const int TokenOverlapBaseScore = 400;
    private const int TokenOverlapWeight = 10;
    private const int EditDistanceBaseScore = 300;

    /// <summary>
    /// Cap on counted shared tokens. With a base of 400 and a weight of 10 this
    /// keeps the largest token-overlap score at 600, below containment's
    /// minimum of 601, so the documented confidence order cannot invert.
    /// </summary>
    private const int TokenOverlapWeightCap = 20;

    /// <summary>A non-null match: the ranking score (higher is closer) and its kind.</summary>
    public sealed record Result(int Score, string Kind);

    /// <summary>
    /// Scores one candidate against the query, or returns <c>null</c> when the
    /// names are not similar. Both arguments must already be folded; blank
    /// inputs never match.
    /// </summary>
    public static Result? Evaluate(string queryNormalized, string candidateNormalized)
    {
        if (string.IsNullOrEmpty(queryNormalized) || string.IsNullOrEmpty(candidateNormalized))
        {
            return null;
        }

        if (string.Equals(queryNormalized, candidateNormalized, StringComparison.Ordinal))
        {
            return new Result(ExactScore, ExactKind);
        }

        var queryTokens = Tokenize(queryNormalized);
        var candidateTokens = Tokenize(candidateNormalized);

        if (IsContiguousSubsequence(queryTokens, candidateTokens)
            || IsContiguousSubsequence(candidateTokens, queryTokens))
        {
            // A longer shared phrase is a stronger signal within this class.
            var phraseLength = Math.Min(queryTokens.Count, candidateTokens.Count);
            return new Result(ContainmentBaseScore + phraseLength, ContainmentKind);
        }

        var shared = CountSharedTokens(queryTokens, candidateTokens);
        if (shared > 0)
        {
            // Clamp the overlap component so a token-overlap match can never
            // outrank containment or exact equality (the documented confidence
            // bands); the cap keeps the maximum overlap score at 600.
            var boundedShared = Math.Min(shared, TokenOverlapWeightCap);
            return new Result(TokenOverlapBaseScore + (boundedShared * TokenOverlapWeight), TokenOverlapKind);
        }

        // Length pruning keeps the edit-distance pass cheap and ignores pairs
        // that cannot be within the allowed distance.
        if (Math.Abs(queryNormalized.Length - candidateNormalized.Length) <= MaxEditDistance)
        {
            var distance = LevenshteinDistance(queryNormalized, candidateNormalized, MaxEditDistance);
            if (distance <= MaxEditDistance)
            {
                return new Result(EditDistanceBaseScore - distance, EditDistanceKind);
            }
        }

        return null;
    }

    /// <summary>Splits a folded name into lowercase alphanumeric tokens.</summary>
    public static IReadOnlyList<string> Tokenize(string normalized)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var index = 0; index < normalized.Length; index++)
        {
            if (char.IsLetterOrDigit(normalized[index]))
            {
                if (start < 0)
                {
                    start = index;
                }
            }
            else if (start >= 0)
            {
                tokens.Add(normalized[start..index]);
                start = -1;
            }
        }

        if (start >= 0)
        {
            tokens.Add(normalized[start..]);
        }

        return tokens;
    }

    private static bool IsContiguousSubsequence(
        IReadOnlyList<string> needle,
        IReadOnlyList<string> haystack)
    {
        if (needle.Count == 0 || needle.Count > haystack.Count)
        {
            return false;
        }

        for (var start = 0; start + needle.Count <= haystack.Count; start++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Count; offset++)
            {
                if (!string.Equals(needle[offset], haystack[start + offset], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Counts distinct tokens (two characters or longer) shared by both lists.
    /// Single-character fragments such as the <c>s</c> left by an apostrophe
    /// are ignored so they cannot create a spurious match on their own.
    /// </summary>
    private static int CountSharedTokens(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var rightSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in right)
        {
            if (token.Length >= 2)
            {
                rightSet.Add(token);
            }
        }

        var counted = new HashSet<string>(StringComparer.Ordinal);
        var shared = 0;
        foreach (var token in left)
        {
            if (token.Length >= 2 && rightSet.Contains(token) && counted.Add(token))
            {
                shared++;
            }
        }

        return shared;
    }

    /// <summary>
    /// Levenshtein distance, abandoned as soon as every cell in a row exceeds
    /// <paramref name="maxDistance"/>. When abandoned it returns
    /// <c>maxDistance + 1</c>; otherwise the exact distance, which may still
    /// exceed <paramref name="maxDistance"/>. Callers only compare against
    /// <paramref name="maxDistance"/>.
    /// </summary>
    private static int LevenshteinDistance(string a, string b, int maxDistance)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var column = 0; column <= b.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= a.Length; row++)
        {
            current[0] = row;
            var rowMinimum = current[0];
            for (var column = 1; column <= b.Length; column++)
            {
                var substitution = a[row - 1] == b[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + substitution);
                rowMinimum = Math.Min(rowMinimum, current[column]);
            }

            if (rowMinimum > maxDistance)
            {
                return maxDistance + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
