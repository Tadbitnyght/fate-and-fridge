using System.Globalization;
using System.Net;
using System.Text;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// Plain-text sanitization for untrusted imported content (docs/M4_PLAN.md §1.7,
/// docs/SECURITY.md §8). Shared by <c>StructuredRecipeParser</c> and the later
/// HTML fallback parser.
/// </summary>
/// <remarks>
/// <para>
/// The pipeline is fixed and idempotent: strip markup, decode HTML entities
/// (bounded to catch entity-encoded markup), remove control and format
/// characters except tab/newline, collapse every whitespace run to one space,
/// trim, and optionally truncate to a caller-supplied limit. The result is
/// plain text and never markup: the output cannot contain a <c>&lt;…&gt;</c>
/// tag and is safe to render as text.
/// </para>
/// <para>
/// <c>String.Normalize</c> is deliberately never used; this workstation runs
/// with invariant globalization, where ICU-backed normalization and casing are
/// silent no-ops. All logic is ordinal and table-free so it is deterministic in
/// both globalization modes.
/// </para>
/// <para>
/// Length limits (M3: name 200, long text 20,000, preparation note 300) are
/// applied by M4-04/M4-05. The optional <paramref name="maxLength"/> on
/// <see cref="Sanitize(string?, int?)"/> is the shared truncation helper those
/// issues can use.
/// </para>
/// </remarks>
public static class ImportTextSanitizer
{
    /// <summary>How many strip/decode rounds are applied to expose encoded markup.</summary>
    private const int MaxStripDecodePasses = 3;

    /// <summary>
    /// Sanitizes one untrusted value into plain text. Returns <c>null</c> when
    /// the input is null or becomes empty after sanitization.
    /// </summary>
    /// <param name="value">Raw untrusted text (may contain HTML markup/entities).</param>
    /// <param name="maxLength">
    /// Optional maximum length; values longer than this are truncated. Ignored
    /// when null or negative.
    /// </param>
    public static string? Sanitize(string? value, int? maxLength = null)
    {
        if (value is null)
        {
            return null;
        }

        var text = StripMarkupAndDecodeEntities(value);
        text = RemoveDisallowedControlCharacters(text);
        text = CollapseWhitespace(text).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (maxLength is int limit && limit >= 0 && text.Length > limit)
        {
            text = text[..limit];
        }

        return text;
    }

    /// <summary>
    /// True when <paramref name="value"/> contains a Unicode control or format
    /// character other than tab, line feed, or a carriage return that precedes a
    /// line feed, or an unpaired UTF-16 surrogate. Exposed so later import
    /// stages can reject rather than silently rewrite such input. Paired
    /// surrogates (a valid non-BMP code point) are not reported.
    /// </summary>
    public static bool ContainsDisallowedControlCharacter(string? value)
    {
        if (value is null)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '\t' or '\n')
            {
                continue;
            }

            if (character == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
            {
                continue;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    index++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(character))
            {
                return true;
            }

            if (char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }

    private static string StripMarkupAndDecodeEntities(string value)
    {
        var current = value;
        for (var pass = 0; pass < MaxStripDecodePasses; pass++)
        {
            var stripped = StripMarkup(current);
            var decoded = WebUtility.HtmlDecode(stripped);
            if (string.Equals(decoded, current, StringComparison.Ordinal))
            {
                break;
            }

            current = decoded;
        }

        // Final strip so a decoded tag introduced on the last pass cannot
        // survive as text resembling markup.
        return StripMarkup(current);
    }

    /// <summary>
    /// Removes anything that looks like an HTML tag, replacing it with a space
    /// so removed block/inline tags cannot join two words. A <c>&lt;</c> only
    /// starts a tag when followed by a letter, <c>/</c>, <c>!</c>, or <c>?</c>,
    /// so a decoded or literal comparison such as <c>1 &lt; 2 &gt; 0</c> is kept
    /// as text. An unterminated tag-start drops the remainder rather than
    /// leaking a partial tag; this keeps the function total and guarantees the
    /// result is not markup.
    /// </summary>
    private static string StripMarkup(string value)
    {
        if (!value.Contains('<', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var index = 0;
        while (index < value.Length)
        {
            var character = value[index];
            if (character == '<' && IsTagStart(value, index + 1))
            {
                var close = value.IndexOf('>', index + 1);
                if (close < 0)
                {
                    break;
                }

                builder.Append(' ');
                index = close + 1;
                continue;
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    private static bool IsTagStart(string value, int index)
    {
        if (index >= value.Length)
        {
            return false;
        }

        var character = value[index];
        return char.IsAsciiLetter(character) || character is '/' or '!' or '?';
    }

    private static string RemoveDisallowedControlCharacters(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\r')
            {
                builder.Append('\n');
                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            if (character is '\t' or '\n')
            {
                builder.Append(character);
                continue;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    builder.Append(character);
                    builder.Append(value[index + 1]);
                    index++;
                }

                // An unpaired high surrogate is dropped.
                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                // An unpaired low surrogate is dropped.
                continue;
            }

            if (char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        return builder.ToString();
    }
}
