namespace FateAndFridge.Api.Imports;

/// <summary>
/// Cheap pre-parse budget guard for the HTML fallback. AngleSharp's DOM construction is superlinear on extremely deep
/// nesting, so the import service estimates the maximum tag nesting depth from
/// the raw HTML before handing it to <see cref="HtmlFallbackParser"/> and skips
/// the fallback when the document is absurdly deep.
/// </summary>
/// <remarks>
/// <para>
/// The estimator is a single forward scan with no allocations: it counts
/// opening tags, decrements on closing tags, and ignores comments, doctypes
/// processing instructions, self-closing/void elements, and the raw-text
/// contents of <c>script</c>/<c>style</c>/<c>title</c>/<c>textarea</c>. A
/// <c>&lt;</c> that does not begin a real tag name (for example <c>&lt; 2</c>
/// <c>&lt;=</c>, or <c>&lt;3</c>) is treated as literal text, and <c>&gt;</c>
/// inside a quoted attribute value does not end the tag. It is deliberately
/// approximate — a guard, not a parser — so false positives only cost a
/// skipped best-effort fallback, never correctness.
/// </para>
/// <para>
/// JSON-LD extraction runs before this guard and is never affected by it; a
/// page rejected here may still yield a JSON-LD recipe.
/// </para>
/// <para>
/// The fallback's own counters (visible-node and text-item caps in
/// <see cref="HtmlFallbackParser"/>) bound total parse work once it is invoked
/// this guard only decides whether to invoke it at all.
/// </para>
/// </remarks>
public static class HtmlDepthGuard
{
    /// <summary>Maximum tag nesting depth considered safe for the fallback.</summary>
    public const int MaxSafeTagDepth = 4000;

    /// <summary>HTML void elements that never open a nesting level.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>
    /// Elements whose contents are raw text: a <c>&lt;</c> inside them is not a
    /// tag and must not be counted.
    /// </summary>
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "title", "textarea",
    };

    /// <summary>
    /// True when the raw HTML's estimated nesting depth is at or below
    /// <see cref="MaxSafeTagDepth"/> and the fallback may run.
    /// </summary>
    public static bool IsWithinDepthBudget(string? html) =>
        EstimateMaxTagDepth(html) <= MaxSafeTagDepth;

    /// <summary>
    /// Estimates the deepest tag nesting in <paramref name="html"/> with a
    /// single scan. Malformed/truncated markup is tolerated and never throws.
    /// </summary>
    public static int EstimateMaxTagDepth(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return 0;
        }

        var depth = 0;
        var maxDepth = 0;

        for (var i = 0; i < html.Length; i++)
        {
            if (html[i] != '<')
            {
                continue;
            }

            var next = i + 1 < html.Length ? html[i + 1] : '\0';
            if (next == '/')
            {
                if (depth > 0)
                {
                    depth--;
                }

                var closingEnd = SkipToTagEnd(html, i + 1);
                if (closingEnd < 0)
                {
                    break;
                }

                i = closingEnd;
                continue;
            }

            if (next == '!')
            {
                // HTML comment: the body frequently contains '>' (and even
                // markup), so scan for the real terminator instead of the next '>'.
                if (i + 3 < html.Length && html[i + 2] == '-' && html[i + 3] == '-')
                {
                    var commentEnd = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (commentEnd < 0)
                    {
                        break;
                    }

                    i = commentEnd + 2;
                    continue;
                }

                var declarationEnd = SkipToTagEnd(html, i + 1);
                if (declarationEnd < 0)
                {
                    break;
                }

                i = declarationEnd;
                continue;
            }

            if (next == '?')
            {
                // Processing instruction: no nesting.
                var instructionEnd = SkipToTagEnd(html, i + 1);
                if (instructionEnd < 0)
                {
                    break;
                }

                i = instructionEnd;
                continue;
            }

            // A real tag must start with an ASCII letter; '<', '<=' and '<3'
            // are literal text, not markup.
            if (!IsAsciiLetter(next))
            {
                continue;
            }

            var nameStart = i + 1;
            var nameEnd = nameStart;
            while (nameEnd < html.Length && IsTagNameCharacter(html[nameEnd]))
            {
                nameEnd++;
            }

            var tagEnd = SkipToTagEnd(html, nameEnd);
            if (tagEnd < 0)
            {
                // Truncated tag; stop scanning rather than guess.
                break;
            }

            var selfClosing = html[tagEnd - 1] == '/';
            var name = html[nameStart..nameEnd];

            if (selfClosing || VoidElements.Contains(name))
            {
                i = tagEnd;
                continue;
            }

            depth++;
            if (depth > maxDepth)
            {
                maxDepth = depth;
            }

            if (RawTextElements.Contains(name))
            {
                // Skip the raw-text body so its '<' characters are not counted
                // resume on the matching closing tag so it still decrements.
                var closeStart = IndexOfCloseTag(html, tagEnd + 1, name);
                if (closeStart < 0)
                {
                    break;
                }

                i = closeStart - 1;
                continue;
            }

            i = tagEnd;
        }

        return maxDepth;
    }

    /// <summary>
    /// Returns the index of the <c>&gt;</c> closing the tag that starts at
    /// <paramref name="from"/>, or <c>-1</c> when the tag is truncated. A
    /// <c>&gt;</c> inside a single- or double-quoted attribute value is skipped.
    /// </summary>
    private static int SkipToTagEnd(string html, int from)
    {
        var quote = '\0';
        for (var i = from; i < html.Length; i++)
        {
            var current = html[i];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                quote = current;
                continue;
            }

            if (current == '>')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the start of the case-insensitive <c>&lt;/name</c> closing tag at
    /// or after <paramref name="from"/>, or <c>-1</c> when absent.
    /// </summary>
    private static int IndexOfCloseTag(string html, int from, string name)
    {
        var needle = string.Concat("</", name);
        var searchFrom = from;
        while (searchFrom < html.Length)
        {
            var index = html.IndexOf(needle, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var after = index + needle.Length;
            if (after >= html.Length || !IsTagNameCharacter(html[after]))
            {
                return index;
            }

            searchFrom = after;
        }

        return -1;
    }

    private static bool IsTagNameCharacter(char value) =>
        value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');

    private static bool IsAsciiLetter(char value) =>
        value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
}
