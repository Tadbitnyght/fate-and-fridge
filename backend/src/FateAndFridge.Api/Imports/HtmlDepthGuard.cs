namespace FateAndFridge.Api.Imports;

/// <summary>
/// Cheap pre-parse budget guard for the HTML fallback (docs/M4_PLAN.md §1.5,
/// §2.1). AngleSharp's DOM construction is superlinear on extremely deep
/// nesting, so the import service estimates the maximum tag nesting depth from
/// the raw HTML before handing it to <see cref="HtmlFallbackParser"/> and skips
/// the fallback when the document is absurdly deep.
/// </summary>
/// <remarks>
/// <para>
/// The estimator is a single forward scan with no allocations: it counts
/// opening tags, decrements on closing tags, and ignores comments, doctypes,
/// processing instructions, and self-closing/void elements. It is deliberately
/// approximate — a guard, not a parser — so false positives only cost a
/// skipped best-effort fallback, never correctness.
/// </para>
/// <para>
/// The fallback's own counters (visible-node and text-item caps in
/// <see cref="HtmlFallbackParser"/>) bound total parse work once it is invoked;
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
            if (!selfClosing && (name.Length == 0 || !VoidElements.Contains(name)))
            {
                depth++;
                if (depth > maxDepth)
                {
                    maxDepth = depth;
                }
            }

            i = tagEnd;
        }

        return maxDepth;
    }

    /// <summary>
    /// Returns the index of the <c>&gt;</c> closing the tag that starts at
    /// <paramref name="from"/>, or <c>-1</c> when the tag is truncated.
    /// </summary>
    private static int SkipToTagEnd(string html, int from)
    {
        var end = html.IndexOf('>', from);
        return end < 0 ? -1 : end;
    }

    private static bool IsTagNameCharacter(char value) =>
        value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9');
}
