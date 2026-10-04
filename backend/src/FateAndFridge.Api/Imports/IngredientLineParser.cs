using System.Globalization;
using System.Text;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// One raw ingredient line split into its parts by <see cref="IngredientLineParser"/>
/// (docs/M4_PLAN.md §1.12). A value that could not be parsed unambiguously stays
/// <c>null</c> (or, for <see cref="Name"/>, empty) rather than being guessed; the
/// full <see cref="Raw"/> line is always preserved.
/// </summary>
/// <param name="Raw">The original (already sanitized) line.</param>
/// <param name="Quantity">Parsed amount, or <c>null</c> for ranges/multipliers/word quantities.</param>
/// <param name="UnitText">Raw unit text after the quantity, or <c>null</c> when absent/unknown.</param>
/// <param name="Name">Remaining ingredient text; empty when the line carried only a quantity.</param>
/// <param name="PreparationNote">Trailing comma segment, or <c>null</c>.</param>
public sealed record ParsedIngredientLine(
    string Raw,
    decimal? Quantity,
    string? UnitText,
    string Name,
    string? PreparationNote);

/// <summary>
/// Pure, dependency-free split of a raw ingredient line into quantity, unit
/// text, name, and preparation note (docs/M4_PLAN.md §1.12). This type reads no
/// database and never throws: malformed input degrades to a preserved name.
/// </summary>
/// <remarks>
/// <para>
/// Quantity forms accepted: integers, decimals, Unicode vulgar fractions
/// (½ ⅓ ¼ ¾ ⅔ ⅛ …), simple <c>n/d</c>, and mixed <c>w n/d</c> or <c>w½</c>.
/// Ranges (<c>1-2</c>, <c>1 to 2</c>, <c>1–2</c>), multipliers (<c>2x</c>), and
/// word quantities (<c>a pinch</c>, <c>some</c>) keep <c>quantity = null</c> and
/// retain the original text in the name rather than inventing a number.
/// </para>
/// <para>
/// Unit extraction matches the longest unit phrase supplied by the caller
/// (already folded to lowercase with trailing/repeated periods removed) at the
/// start of the remaining text, falling back to the shared
/// <see cref="ImportUnitText"/> dimension hints so an unrecognized unit can
/// still be flagged by the normalizer. A parenthetical package size
/// (<c>(14 oz)</c>) is preserved and prefixed onto the name.
/// </para>
/// </remarks>
public static class IngredientLineParser
{
    private const int MaxUnitPhraseTokens = 4;

    /// <summary>Unicode vulgar fractions mapped to their decimal value.</summary>
    private static readonly Dictionary<char, decimal> VulgarFractions = new()
    {
        ['\u00BD'] = 0.5m,      // ½
        ['\u2153'] = 1m / 3m,   // ⅓
        ['\u2154'] = 2m / 3m,   // ⅔
        ['\u00BC'] = 0.25m,     // ¼
        ['\u00BE'] = 0.75m,     // ¾
        ['\u2155'] = 0.2m,      // ⅕
        ['\u2156'] = 0.4m,      // ⅖
        ['\u2157'] = 0.6m,      // ⅗
        ['\u2158'] = 0.8m,      // ⅘
        ['\u2159'] = 1m / 6m,   // ⅙
        ['\u215A'] = 5m / 6m,   // ⅚
        ['\u2150'] = 1m / 7m,   // ⅐
        ['\u215B'] = 0.125m,    // ⅛
        ['\u215C'] = 0.375m,    // ⅜
        ['\u215D'] = 0.625m,    // ⅝
        ['\u215E'] = 0.875m,    // ⅞
        ['\u2151'] = 1m / 9m,   // ⅑
        ['\u2152'] = 0.1m,      // ⅒
    };

    /// <summary>Leading words that stand in for a quantity and must not be turned into a number.</summary>
    private static readonly HashSet<string> WordQuantityPrefixes =
        new(StringComparer.Ordinal) { "a", "an", "some", "few", "several", "couple" };

    /// <summary>
    /// Parses one ingredient line. Never throws.
    /// </summary>
    /// <param name="rawLine">Raw (ideally already sanitized) line; may be null/blank.</param>
    /// <param name="knownUnitPhrases">
    /// Folded (lowercase, periods removed) unit symbols/aliases used to find the
    /// longest unit phrase after a parsed quantity. May be null or empty.
    /// </param>
    public static ParsedIngredientLine Parse(
        string? rawLine,
        IReadOnlyCollection<string>? knownUnitPhrases = null)
    {
        var raw = rawLine ?? string.Empty;
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return new ParsedIngredientLine(raw, null, null, string.Empty, null);
        }

        var (head, preparationNote) = SplitPreparationNote(trimmed);
        head = head.Trim();
        if (head.Length == 0)
        {
            return new ParsedIngredientLine(raw, null, null, string.Empty, preparationNote);
        }

        var hasQuantity = TryParseQuantityAt(head, 0, out var quantity, out var consumed);
        if (hasQuantity)
        {
            if (IsRange(head, consumed) || IsMultiplier(head, consumed))
            {
                // Ranges/multipliers are preserved verbatim; no unit token is
                // split off because the leading token is not a clean quantity.
                return new ParsedIngredientLine(raw, null, null, head, preparationNote);
            }

            var remainder = head[consumed..].TrimStart();
            var (parentheses, afterParentheses) = CollectLeadingParentheticals(remainder);
            var (unitText, rest) = SplitUnit(afterParentheses, knownUnitPhrases);
            var name = JoinParenthesesAndRest(parentheses, rest);
            return new ParsedIngredientLine(raw, quantity, unitText, name, preparationNote);
        }

        if (StartsWithWordQuantity(head))
        {
            return new ParsedIngredientLine(raw, null, null, head, preparationNote);
        }

        // No leading quantity: keep the whole head as the name and do not guess a unit.
        return new ParsedIngredientLine(raw, null, null, head, preparationNote);
    }

    /// <summary>Splits the trailing top-level comma segment off as the preparation note.</summary>
    private static (string Head, string? Note) SplitPreparationNote(string text)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '(' or '[')
            {
                depth++;
            }
            else if (character is ')' or ']')
            {
                if (depth > 0)
                {
                    depth--;
                }
            }
            else if (character == ',' && depth == 0)
            {
                var head = text[..index];
                var note = text[(index + 1)..].Trim();
                return (head, note.Length == 0 ? null : note);
            }
        }

        return (text, null);
    }

    /// <summary>
    /// True when the token following the parsed quantity is a range separator
    /// (<c>-</c>, en/em dash, or <c>to</c>) followed by another quantity.
    /// </summary>
    private static bool IsRange(string text, int afterQuantity)
    {
        var separator = SkipWhitespace(text, afterQuantity);
        if (separator >= text.Length)
        {
            return false;
        }

        int afterSeparator;
        if (IsDash(text[separator]))
        {
            afterSeparator = separator + 1;
        }
        else if ((text[separator] is 't' or 'T')
            && separator + 1 < text.Length
            && (text[separator + 1] is 'o' or 'O')
            && (separator + 2 >= text.Length || !char.IsLetter(text[separator + 2])))
        {
            afterSeparator = separator + 2;
        }
        else
        {
            return false;
        }

        return TryParseQuantityAt(text, afterSeparator, out _, out _);
    }

    /// <summary>
    /// Dash-like separators accepted in ranges: hyphen-minus, hyphen, non-breaking
    /// hyphen, figure dash, en dash, em dash, horizontal bar, minus sign, and
    /// fullwidth hyphen-minus.
    /// </summary>
    private static bool IsDash(char character) => character
        is '-' or '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' or '\uFF0D';

    /// <summary>True when the trailing token is a multiplier such as <c>2x</c>.</summary>
    private static bool IsMultiplier(string text, int afterQuantity)
    {
        var index = SkipWhitespace(text, afterQuantity);
        if (index >= text.Length || text[index] is not ('x' or 'X' or '\u00D7'))
        {
            return false;
        }

        var end = index + 1;
        return end >= text.Length
            || char.IsWhiteSpace(text[end])
            || char.IsAsciiDigit(text[end])
            || text[end] == '(';
    }

    private static bool StartsWithWordQuantity(string text)
    {
        var end = 0;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        if (end == 0 || !WordQuantityPrefixes.Contains(text[..end].ToLowerInvariant()))
        {
            return false;
        }

        return end >= text.Length || char.IsWhiteSpace(text[end]) || text[end] == ',';
    }

    /// <summary>Collects leading balanced parenthetical groups (package sizes) and the remaining text.</summary>
    private static (List<string> Parentheses, string Remaining) CollectLeadingParentheticals(string text)
    {
        var parentheses = new List<string>();
        var index = 0;
        while (true)
        {
            index = SkipWhitespace(text, index);
            if (index >= text.Length || text[index] != '(')
            {
                break;
            }

            var close = FindMatchingParenthesis(text, index);
            if (close < 0)
            {
                break;
            }

            parentheses.Add(text[index..(close + 1)]);
            index = close + 1;
        }

        return (parentheses, text[index..].TrimStart());
    }

    private static int FindMatchingParenthesis(string text, int openIndex)
    {
        var depth = 0;
        for (var index = openIndex; index < text.Length; index++)
        {
            if (text[index] == '(')
            {
                depth++;
            }
            else if (text[index] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Splits the leading unit phrase off <paramref name="text"/>, preferring the
    /// longest known phrase, then a single-token dimension hint.
    /// </summary>
    private static (string? UnitText, string Remaining) SplitUnit(
        string text,
        IReadOnlyCollection<string>? knownUnitPhrases)
    {
        if (text.Length == 0)
        {
            return (null, text);
        }

        var tokens = Tokenize(text);
        if (knownUnitPhrases is { Count: > 0 })
        {
            var phraseSet = new HashSet<string>(knownUnitPhrases, StringComparer.Ordinal);
            var max = Math.Min(MaxUnitPhraseTokens, tokens.Count);
            for (var length = max; length >= 1; length--)
            {
                var candidate = JoinTokens(tokens, length);
                if (phraseSet.Contains(ImportUnitText.Fold(candidate)))
                {
                    return (candidate, text[tokens[length - 1].End..].TrimStart());
                }
            }
        }

        var first = tokens[0];
        var folded = ImportUnitText.Fold(first.Text);
        if (folded.Length > 0 && ImportUnitText.TryGetDimension(folded, out _))
        {
            return (first.Text, text[first.End..].TrimStart());
        }

        return (null, text);
    }

    private static string JoinParenthesesAndRest(List<string> parentheses, string rest)
    {
        if (parentheses.Count == 0)
        {
            return rest;
        }

        var joined = string.Join(' ', parentheses);
        return rest.Length == 0 ? joined : joined + " " + rest;
    }

    /// <summary>Attempts to parse any supported quantity form at <paramref name="start"/>.</summary>
    private static bool TryParseQuantityAt(string text, int start, out decimal value, out int consumed)
    {
        value = 0m;
        consumed = 0;

        var index = SkipWhitespace(text, start);
        if (index >= text.Length)
        {
            return false;
        }

        var afterWhole = ScanDigits(text, index);

        // Mixed number: "1 1/2" or "1 ½". The addition of the whole and the
        // fraction is guarded so an out-of-range value returns "unparseable"
        // instead of throwing OverflowException.
        if (afterWhole > index)
        {
            var fractionStart = SkipWhitespace(text, afterWhole);
            if (fractionStart > afterWhole
                && TryParseFractionAt(text, fractionStart, out var mixedFraction, out var mixedConsumed)
                && TryParseWhole(text, index, afterWhole, out var whole))
            {
                return TryAdd(text, whole, mixedFraction, mixedConsumed, out value, out consumed);
            }

            if (fractionStart > afterWhole
                && LooksLikeMalformedFraction(text, fractionStart))
            {
                // A whitespace-separated numeric token with a slash that did not
                // parse (for example an overflowing denominator) is malformed;
                // the whole token must not degrade to its integer prefix.
                return false;
            }

            if (fractionStart < text.Length
                && VulgarFractions.TryGetValue(text[fractionStart], out var mixedVulgar)
                && TryParseWhole(text, index, afterWhole, out var vulgarWhole))
            {
                return TryAdd(text, vulgarWhole, mixedVulgar, fractionStart + 1, out value, out consumed);
            }
        }

        // Bare vulgar fraction.
        if (VulgarFractions.TryGetValue(text[index], out var alone))
        {
            return Accept(text, alone, index + 1, out value, out consumed);
        }

        // Simple fraction "1/2".
        if (TryParseFractionAt(text, index, out var fraction, out var fractionConsumed))
        {
            return Accept(text, fraction, fractionConsumed, out value, out consumed);
        }

        // Decimal "2.5" (requires a decimal point).
        var decimalEnd = ScanDecimal(text, index);
        if (decimalEnd > index && text.AsSpan(index, decimalEnd - index).Contains('.'))
        {
            if (decimal.TryParse(
                    text.AsSpan(index, decimalEnd - index),
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsedDecimal))
            {
                return Accept(text, parsedDecimal, decimalEnd, out value, out consumed);
            }
        }

        // Integer.
        if (afterWhole > index && TryParseWhole(text, index, afterWhole, out var integer))
        {
            return Accept(text, integer, afterWhole, out value, out consumed);
        }

        return false;
    }

    /// <summary>Adds a whole number and a fraction without ever throwing.</summary>
    private static bool TryAdd(
        string text,
        decimal whole,
        decimal fraction,
        int candidateConsumed,
        out decimal value,
        out int consumed)
    {
        decimal sum;
        try
        {
            sum = whole + fraction;
        }
        catch (OverflowException)
        {
            value = 0m;
            consumed = 0;
            return false;
        }

        return Accept(text, sum, candidateConsumed, out value, out consumed);
    }

    /// <summary>
    /// Accepts a parsed quantity unless the token continues with an
    /// unconsumed <c>.</c> or <c>/</c>, which marks a malformed number
    /// (<c>1.2.3</c>, <c>1..2</c>, <c>1/2/3</c>) that must stay unparseable.
    /// </summary>
    private static bool Accept(
        string text,
        decimal candidate,
        int candidateConsumed,
        out decimal value,
        out int consumed)
    {
        if (candidateConsumed < text.Length && text[candidateConsumed] is '/' or '.')
        {
            value = 0m;
            consumed = 0;
            return false;
        }

        value = candidate;
        consumed = candidateConsumed;
        return true;
    }

    /// <summary>True when the token at <paramref name="start"/> looks like a malformed numeric fraction.</summary>
    private static bool LooksLikeMalformedFraction(string text, int start)
    {
        if (start >= text.Length || !char.IsAsciiDigit(text[start]))
        {
            return false;
        }

        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return text.AsSpan(start, end - start).Contains('/');
    }

    private static bool TryParseFractionAt(string text, int start, out decimal value, out int consumed)
    {
        value = 0m;
        consumed = 0;

        var numeratorEnd = ScanDigits(text, start);
        if (numeratorEnd == start)
        {
            return false;
        }

        var slash = SkipWhitespace(text, numeratorEnd);
        if (slash >= text.Length || text[slash] != '/')
        {
            return false;
        }

        var denominatorStart = SkipWhitespace(text, slash + 1);
        var denominatorEnd = ScanDigits(text, denominatorStart);
        if (denominatorEnd == denominatorStart)
        {
            return false;
        }

        if (!TryParseWhole(text, start, numeratorEnd, out var numerator)
            || !TryParseWhole(text, denominatorStart, denominatorEnd, out var denominator)
            || denominator == 0m)
        {
            return false;
        }

        value = numerator / denominator;
        consumed = denominatorEnd;
        return true;
    }

    private static bool TryParseWhole(string text, int start, int end, out decimal value) =>
        decimal.TryParse(
            text.AsSpan(start, end - start),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);

    private static int ScanDigits(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsAsciiDigit(text[index]))
        {
            index++;
        }

        return index;
    }

    private static int ScanDecimal(string text, int start)
    {
        var index = start;
        var seenDigit = false;
        var seenDot = false;
        while (index < text.Length)
        {
            var character = text[index];
            if (char.IsAsciiDigit(character))
            {
                seenDigit = true;
                index++;
            }
            else if (character == '.' && !seenDot)
            {
                seenDot = true;
                index++;
            }
            else
            {
                break;
            }
        }

        return seenDigit ? index : start;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static List<(string Text, int End)> Tokenize(string text)
    {
        var tokens = new List<(string Text, int End)>();
        var index = 0;
        while (index < text.Length)
        {
            index = SkipWhitespace(text, index);
            if (index >= text.Length)
            {
                break;
            }

            var start = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            tokens.Add((text[start..index], index));
        }

        return tokens;
    }

    private static string JoinTokens(List<(string Text, int End)> tokens, int count)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(tokens[index].Text);
        }

        return builder.ToString();
    }
}
