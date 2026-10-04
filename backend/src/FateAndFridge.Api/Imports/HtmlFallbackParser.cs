using System.Text;

using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// Best-effort extraction of schema.org <c>Recipe</c> data from a page that has
/// no JSON-LD <c>Recipe</c> (docs/M4_PLAN.md §1.5, §2.1;
/// docs/PRODUCT_SPEC.md §28; docs/SECURITY.md §7–§8).
/// </summary>
/// <remarks>
/// <para>
/// The page is parsed once with AngleSharp into a static DOM. Scripting is
/// disabled and no external resource is fetched, so no third-party JavaScript
/// runs and no network access happens during parsing (docs/SECURITY.md §7).
/// Microdata is tried first (<c>itemtype</c> containing
/// <c>schema.org/Recipe</c>); only when it yields no recipe are conservative
/// heading/list heuristics used. Ambiguous values are left null/empty rather
/// than guessed (docs/M4_PLAN.md §1.4).
/// </para>
/// <para>
/// Parsing is total: malformed, truncated, or hostile markup is handled by the
/// HTML parser and any unexpected AngleSharp failure is swallowed, yielding an
/// empty result rather than an exception.
/// </para>
/// <para>
/// Every returned string has already passed through
/// <see cref="ImportTextSanitizer"/>. ISO-8601 durations reuse
/// <see cref="StructuredRecipeParser.TryParseIso8601Minutes"/>.
/// </para>
/// <para>
/// Known limitations, deliberately not over-engineered: microdata
/// <c>itemref</c> (properties referenced from outside the declaring item scope)
/// is not followed; the heuristics match class/id substrings (so
/// <c>class="ingredients-note"</c> can match) and take the first list after the
/// matching section heading. Both are best-effort and never invent data.
/// </para>
/// </remarks>
public static class HtmlFallbackParser
{
    private const string RecipeType = "Recipe";
    private const string HowToStepType = "HowToStep";
    private const string HowToSectionType = "HowToSection";

    /// <summary>Upper bound on recipe roots returned from one page.</summary>
    private const int MaxRecipes = 100;

    /// <summary>Upper bound on collected ingredient/instruction strings.</summary>
    private const int MaxTextItems = 2_000;

    /// <summary>Recursion guard for pathologically nested instruction markup.</summary>
    private const int MaxInstructionDepth = 32;

    /// <summary>
    /// Elements whose text is never recipe content and must be skipped when
    /// reading visible text (docs/SECURITY.md §8).
    /// </summary>
    private static readonly HashSet<string> NonTextElementNames =
        new(StringComparer.OrdinalIgnoreCase) { "script", "style", "template", "noscript", "head" };

    private static readonly string[] IngredientKeywords = ["ingredient"];

    private static readonly string[] InstructionKeywords =
        ["instruction", "direction", "method", "preparation", "steps"];

    /// <summary>
    /// Parses every plausible recipe from a page's microdata, falling back to
    /// heading/list heuristics when the page declares no microdata recipe.
    /// Returns an empty list when nothing plausible is found.
    /// </summary>
    /// <param name="html">Raw page HTML (already fetched and size-checked).</param>
    /// <param name="pageUrl">
    /// URL the page was fetched from; used as the default
    /// <see cref="ParsedRecipe.SourceUrl"/>.
    /// </param>
    public static IReadOnlyList<ParsedRecipe> Parse(string? html, string? pageUrl = null)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        var sourceUrl = ImportTextSanitizer.Sanitize(pageUrl);

        IDocument document;
        try
        {
            var parser = new HtmlParser(new HtmlParserOptions { IsScripting = false });
            document = parser.ParseDocument(html);
        }
        catch (Exception)
        {
            return [];
        }

        try
        {
            var microdata = ParseMicrodata(document, sourceUrl);
            if (microdata.Count > 0)
            {
                return microdata;
            }

            var heuristic = ParseHeuristics(document, sourceUrl);
            return heuristic is null ? [] : [heuristic];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IReadOnlyList<ParsedRecipe> ParseMicrodata(IDocument document, string? sourceUrl)
    {
        var candidates = document.QuerySelectorAll("[itemtype]")
            .Where(IsRecipeElement)
            .ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        // A recipe nested inside another recipe is the same candidate; only the
        // outermost root is returned so it cannot be counted twice.
        var roots = new List<IElement>();
        foreach (var candidate in candidates)
        {
            var nested = candidates.Any(
                other => !ReferenceEquals(other, candidate) && IsDescendantOf(candidate, other));
            if (!nested)
            {
                roots.Add(candidate);
            }

            if (roots.Count >= MaxRecipes)
            {
                break;
            }
        }

        var recipes = new List<ParsedRecipe>(roots.Count);
        foreach (var root in roots)
        {
            recipes.Add(MapMicrodataRecipe(root, sourceUrl));
        }

        return recipes;
    }

    private static ParsedRecipe MapMicrodataRecipe(IElement root, string? sourceUrl) => new()
    {
        Name = FirstOwnPropertyValue(root, "name"),
        Description = FirstOwnPropertyValue(root, "description"),
        Author = ReadMicrodataAuthor(root),
        SourceUrl = sourceUrl,
        ImageUrl = ReadMicrodataImage(root),
        PrepMinutes = ParseDuration(FirstOwnPropertyValue(root, "prepTime")),
        CookMinutes = ParseDuration(FirstOwnPropertyValue(root, "cookTime")),
        IngredientLines = ReadMicrodataIngredients(root),
        InstructionSections = ReadMicrodataInstructions(root),
    };

    private static string? FirstOwnPropertyValue(IElement root, string propertyName)
    {
        foreach (var element in OwnPropertyElements(root, propertyName))
        {
            var value = ReadOwnValue(element);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadMicrodataAuthor(IElement root)
    {
        var names = new List<string>();
        foreach (var element in OwnPropertyElements(root, "author"))
        {
            var nameElement = FindPropertyInScope(element, "name");
            var value = nameElement is not null ? ReadOwnValue(nameElement) : ReadOwnValue(element);
            if (value is not null)
            {
                names.Add(value);
            }
        }

        return names.Count == 0
            ? null
            : ImportTextSanitizer.Sanitize(string.Join(", ", names));
    }

    private static string? ReadMicrodataImage(IElement root)
    {
        foreach (var element in OwnPropertyElements(root, "image"))
        {
            // An ImageObject carries its URL in a nested (out-of-scope) property.
            var nested = FindProperty(element, "url") ?? FindProperty(element, "contentUrl");
            var value = nested is not null ? ReadOwnValue(nested) : null;
            value ??= ReadOwnValue(element);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> ReadMicrodataIngredients(IElement root)
    {
        var lines = new List<string>();
        CollectIngredientLines(root, "recipeIngredient", lines);
        if (lines.Count == 0)
        {
            CollectIngredientLines(root, "ingredients", lines);
        }

        return lines;
    }

    private static void CollectIngredientLines(IElement root, string propertyName, List<string> lines)
    {
        foreach (var element in OwnPropertyElements(root, propertyName))
        {
            var value = ReadOwnValue(element);
            if (value is not null)
            {
                lines.Add(value);
                if (lines.Count >= MaxTextItems)
                {
                    return;
                }
            }
        }
    }

    private static IReadOnlyList<ParsedInstructionSection> ReadMicrodataInstructions(IElement root)
    {
        var accumulator = new InstructionAccumulator();

        var holders = OwnPropertyElements(root, "recipeInstructions").ToList();
        if (holders.Count > 0)
        {
            foreach (var holder in holders)
            {
                CollectInstructionNode(holder, accumulator, 0);
            }

            return accumulator.Build();
        }

        // Some pages mark steps with HowToStep/HowToSection but omit the
        // recipeInstructions property; the typed steps are still a strong
        // enough signal to keep.
        foreach (var node in FindOutermostInstructionNodes(root))
        {
            CollectInstructionNode(node, accumulator, 0);
        }

        return accumulator.Build();
    }

    private static void CollectInstructionNode(
        IElement element,
        InstructionAccumulator accumulator,
        int depth)
    {
        if (depth > MaxInstructionDepth)
        {
            return;
        }

        if (IsSchemaType(element, HowToStepType))
        {
            var step = ReadStepText(element);
            if (step is not null)
            {
                accumulator.AddStep(step);
            }

            return;
        }

        if (IsSchemaType(element, HowToSectionType))
        {
            CollectSection(element, accumulator, depth);
            return;
        }

        var units = FindInstructionUnits(element);
        if (units.Count > 0)
        {
            foreach (var unit in units)
            {
                CollectInstructionNode(unit, accumulator, depth + 1);
            }

            return;
        }

        var own = ReadOwnValue(element);
        if (own is not null)
        {
            accumulator.AddStep(own);
        }
    }

    private static void CollectSection(
        IElement section,
        InstructionAccumulator accumulator,
        int depth)
    {
        var titleElement = FindPropertyInScope(section, "name");
        var title = titleElement is not null ? ReadOwnValue(titleElement) : null;

        var steps = new List<string>();
        var nestedSections = new List<IElement>();
        foreach (var node in FindOutermostInstructionNodes(section))
        {
            if (IsSchemaType(node, HowToSectionType))
            {
                nestedSections.Add(node);
                continue;
            }

            var step = ReadStepText(node);
            if (step is not null)
            {
                steps.Add(step);
            }
        }

        if (title is not null || steps.Count > 0)
        {
            accumulator.AddSection(title, steps);
        }

        if (depth >= MaxInstructionDepth)
        {
            return;
        }

        foreach (var nested in nestedSections)
        {
            CollectSection(nested, accumulator, depth + 1);
        }
    }

    private static string? ReadStepText(IElement element)
    {
        var textElement = FindPropertyInScope(element, "text")
            ?? FindPropertyInScope(element, "name");
        return textElement is not null ? ReadOwnValue(textElement) : ReadOwnValue(element);
    }

    /// <summary>
    /// Finds the outermost <c>HowToStep</c>/<c>HowToSection</c> descendants in
    /// document order, skipping any node nested inside one already found.
    /// </summary>
    private static IReadOnlyList<IElement> FindOutermostInstructionNodes(IElement scope)
    {
        var result = new List<IElement>();
        foreach (var candidate in scope.QuerySelectorAll("[itemtype]"))
        {
            if (!IsSchemaType(candidate, HowToStepType)
                && !IsSchemaType(candidate, HowToSectionType))
            {
                continue;
            }

            var nested = result.Any(existing => IsDescendantOf(candidate, existing));
            if (!nested)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>
    /// Instruction units of a <c>recipeInstructions</c> holder in document
    /// order: the outermost typed <c>HowToStep</c>/<c>HowToSection</c> nodes plus
    /// any plain <c>&lt;li&gt;</c>/<c>&lt;p&gt;</c> steps that sit outside those
    /// typed nodes. Mixing the two shapes on one page must not drop the plain
    /// steps.
    /// </summary>
    private static IReadOnlyList<IElement> FindInstructionUnits(IElement scope)
    {
        var typed = FindOutermostInstructionNodes(scope);
        var typedSet = new HashSet<IElement>(typed, ReferenceEqualityComparer.Instance);
        var units = new List<IElement>();

        foreach (var candidate in scope.QuerySelectorAll("*"))
        {
            if (typedSet.Contains(candidate))
            {
                units.Add(candidate);
                continue;
            }

            // Inside or wrapping a typed node: container wrappers are not steps.
            if (typed.Any(node => IsDescendantOf(candidate, node))
                || typed.Any(node => IsDescendantOf(node, candidate)))
            {
                continue;
            }

            if (candidate.LocalName is "li" or "p")
            {
                if (units.Any(unit => IsDescendantOf(candidate, unit)))
                {
                    continue;
                }

                units.Add(candidate);
            }
        }

        return units;
    }

    private static ParsedRecipe? ParseHeuristics(IDocument document, string? sourceUrl)
    {
        var name = SanitizeElementText(document.QuerySelector("h1"))
            ?? ReadMeta(document, "og:title");
        var ingredients = FindIngredients(document);
        var steps = FindInstructions(document);

        // A page with neither an ingredient list nor instructions is not a
        // plausible recipe; never invent one from a title alone.
        if (name is null || (ingredients.Count == 0 && steps.Count == 0))
        {
            return null;
        }

        return new ParsedRecipe
        {
            Name = name,
            Description = ReadMeta(document, "og:description")
                ?? ReadMeta(document, "description"),
            SourceUrl = sourceUrl,
            ImageUrl = ReadMeta(document, "og:image") ?? ReadMeta(document, "image"),
            IngredientLines = ingredients,
            InstructionSections = steps.Count == 0
                ? []
                : [new ParsedInstructionSection(null, steps.ToArray())],
        };
    }

    private static IReadOnlyList<string> FindIngredients(IDocument document)
    {
        var heading = FindSectionHeading(document, IngredientKeywords);
        if (heading is not null)
        {
            var items = CollectListAfter(document, heading);
            if (items.Count > 0)
            {
                return items;
            }
        }

        return FindItemsInLabelledContainer(document, IngredientKeywords, includeParagraphs: false);
    }

    private static IReadOnlyList<string> FindInstructions(IDocument document)
    {
        var heading = FindSectionHeading(document, InstructionKeywords);
        if (heading is not null)
        {
            var items = CollectListAfter(document, heading);
            if (items.Count > 0)
            {
                return items;
            }

            var paragraphs = CollectParagraphsAfter(document, heading);
            if (paragraphs.Count > 0)
            {
                return paragraphs;
            }
        }

        return FindItemsInLabelledContainer(document, InstructionKeywords, includeParagraphs: true);
    }

    private static IElement? FindSectionHeading(IDocument document, string[] keywords)
    {
        foreach (var heading in document.QuerySelectorAll("h1, h2, h3, h4, h5, h6, dt"))
        {
            var text = ImportTextSanitizer.Sanitize(GetVisibleText(heading));
            if (text is null || text.Length > 80)
            {
                continue;
            }

            if (keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                return heading;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> CollectListAfter(IDocument document, IElement heading)
    {
        var all = document.All;
        var start = IndexOf(all, heading);
        if (start < 0)
        {
            return [];
        }

        for (var index = start + 1; index < all.Length; index++)
        {
            var element = all[index];
            if (IsHeadingElement(element))
            {
                break;
            }

            if (element.LocalName is "ul" or "ol")
            {
                var items = SanitizeChildren(element, "li");
                if (items.Count > 0)
                {
                    return items;
                }
            }
        }

        return [];
    }

    private static IReadOnlyList<string> CollectParagraphsAfter(IDocument document, IElement heading)
    {
        var all = document.All;
        var start = IndexOf(all, heading);
        if (start < 0)
        {
            return [];
        }

        var result = new List<string>();
        for (var index = start + 1; index < all.Length; index++)
        {
            var element = all[index];
            if (IsHeadingElement(element))
            {
                break;
            }

            if (element.LocalName == "p")
            {
                var text = ImportTextSanitizer.Sanitize(GetVisibleText(element));
                if (text is not null)
                {
                    result.Add(text);
                    if (result.Count >= MaxTextItems)
                    {
                        break;
                    }
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<string> FindItemsInLabelledContainer(
        IDocument document,
        string[] keywords,
        bool includeParagraphs)
    {
        foreach (var keyword in keywords)
        {
            foreach (var element in document.All)
            {
                if (!ClassOrIdContains(element, keyword))
                {
                    continue;
                }

                var items = SanitizeChildren(element, "li");
                if (items.Count > 0)
                {
                    return items;
                }

                if (includeParagraphs)
                {
                    var paragraphs = SanitizeChildren(element, "p");
                    if (paragraphs.Count > 0)
                    {
                        return paragraphs;
                    }
                }
            }
        }

        return [];
    }

    private static IReadOnlyList<string> SanitizeChildren(IElement container, string tagName)
    {
        var result = new List<string>();
        foreach (var element in container.QuerySelectorAll(tagName))
        {
            var text = ImportTextSanitizer.Sanitize(GetVisibleText(element));
            if (text is not null)
            {
                result.Add(text);
                if (result.Count >= MaxTextItems)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static string? ReadMeta(IDocument document, string key)
    {
        foreach (var meta in document.QuerySelectorAll("meta"))
        {
            var name = meta.GetAttribute("property") ?? meta.GetAttribute("name");
            if (name is null || !string.Equals(name.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = ImportTextSanitizer.Sanitize(meta.GetAttribute("content"));
            if (content is not null)
            {
                return content;
            }
        }

        return null;
    }

    private static string? SanitizeElementText(IElement? element) =>
        element is null ? null : ImportTextSanitizer.Sanitize(GetVisibleText(element));

    private static int? ParseDuration(string? value) =>
        StructuredRecipeParser.TryParseIso8601Minutes(value, out var minutes) ? minutes : null;

    private static bool ClassOrIdContains(IElement element, string keyword)
    {
        var classes = element.GetAttribute("class");
        if (classes is not null && classes.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var id = element.Id;
        return !string.IsNullOrEmpty(id)
            && id.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeadingElement(IElement element) =>
        element.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

    private static int IndexOf(IHtmlCollection<IElement> elements, IElement target)
    {
        for (var index = 0; index < elements.Length; index++)
        {
            if (ReferenceEquals(elements[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsRecipeElement(IElement element) => IsSchemaType(element, RecipeType);

    private static bool IsSchemaType(IElement element, string typeName)
    {
        var value = element.GetAttribute("itemtype");
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var token in SplitTokens(value))
        {
            if (MatchesSchemaType(token, typeName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches a bare schema.org type name or a schema.org URL form
    /// (<c>https://schema.org/Recipe</c>, <c>schema:Recipe</c>), ignoring case.
    /// </summary>
    private static bool MatchesSchemaType(string token, string typeName)
    {
        var trimmed = token.Trim().TrimEnd('/', '#');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var lastSeparator = Math.Max(
            Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('#')),
            trimmed.LastIndexOf(':'));
        var localName = lastSeparator >= 0 && lastSeparator < trimmed.Length - 1
            ? trimmed[(lastSeparator + 1)..]
            : trimmed;
        return string.Equals(localName, typeName, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SplitTokens(string value) =>
        value.Split(
            [' ', '\t', '\n', '\r', '\f'],
            StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// True when <paramref name="element"/> declares <paramref name="propertyName"/>
    /// as one of its whitespace-separated <c>itemprop</c> tokens, accepting bare
    /// names and schema.org URL forms.
    /// </summary>
    private static bool HasItempropToken(IElement element, string propertyName)
    {
        var value = element.GetAttribute("itemprop");
        if (value is null)
        {
            return false;
        }

        foreach (var token in SplitTokens(value))
        {
            if (MatchesSchemaType(token, propertyName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Property elements that belong to <paramref name="root"/>'s own microdata
    /// scope. Elements inside a nested <c>itemscope</c> (an author, image, or
    /// step) are excluded so a recipe's <c>name</c> is never taken from an
    /// author's or step's <c>name</c>.
    /// </summary>
    private static IEnumerable<IElement> OwnPropertyElements(IElement root, string propertyName)
    {
        if (HasItempropToken(root, propertyName))
        {
            yield return root;
        }

        foreach (var element in root.QuerySelectorAll("[itemprop]"))
        {
            if (HasItempropToken(element, propertyName) && IsInScope(element, root))
            {
                yield return element;
            }
        }
    }

    private static IElement? FindPropertyInScope(IElement scope, string propertyName)
    {
        if (HasItempropToken(scope, propertyName))
        {
            return scope;
        }

        foreach (var element in scope.QuerySelectorAll("[itemprop]"))
        {
            if (HasItempropToken(element, propertyName) && IsInScope(element, scope))
            {
                return element;
            }
        }

        return null;
    }

    /// <summary>First matching property at any depth (used inside nested itemscopes).</summary>
    private static IElement? FindProperty(IElement scope, string propertyName)
    {
        if (HasItempropToken(scope, propertyName))
        {
            return scope;
        }

        foreach (var element in scope.QuerySelectorAll("[itemprop]"))
        {
            if (HasItempropToken(element, propertyName))
            {
                return element;
            }
        }

        return null;
    }

    private static bool IsInScope(IElement element, IElement root)
    {
        for (var parent = element.ParentElement;
             parent is not null && !ReferenceEquals(parent, root);
             parent = parent.ParentElement)
        {
            if (parent.HasAttribute("itemscope"))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDescendantOf(IElement element, IElement ancestor)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (ReferenceEquals(parent, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Text rendered to a user, excluding descendants that must never contribute
    /// recipe content (<c>script</c>, <c>style</c>, <c>template</c>,
    /// <c>noscript</c>, and <c>head</c>; docs/SECURITY.md §8). Unlike
    /// <c>TextContent</c> this cannot leak script/style source into a field.
    /// </summary>
    private static string GetVisibleText(IElement element)
    {
        var builder = new StringBuilder();
        AppendVisibleText(element, builder);
        return builder.ToString();
    }

    private static void AppendVisibleText(INode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                builder.Append(child.NodeValue);
                continue;
            }

            if (child is IElement element && !NonTextElementNames.Contains(element.LocalName))
            {
                AppendVisibleText(element, builder);
            }
        }
    }

    /// <summary>
    /// Reads a microdata value from the first applicable attribute, then text:
    /// <c>content</c>, <c>datetime</c>, visible text, then <c>src</c>/<c>href</c>.
    /// This yields an image <c>src</c> for an <c>&lt;img&gt;</c> while preferring
    /// an anchor's visible text over its URL.
    /// </summary>
    private static string? ReadOwnValue(IElement element)
    {
        var direct = ImportTextSanitizer.Sanitize(element.GetAttribute("content"))
            ?? ImportTextSanitizer.Sanitize(element.GetAttribute("datetime"));
        if (direct is not null)
        {
            return direct;
        }

        var text = ImportTextSanitizer.Sanitize(GetVisibleText(element));
        if (text is not null)
        {
            return text;
        }

        return ImportTextSanitizer.Sanitize(element.GetAttribute("src"))
            ?? ImportTextSanitizer.Sanitize(element.GetAttribute("href"));
    }

    /// <summary>
    /// Groups instruction steps and titled sections while preserving source
    /// order, mirroring the JSON-LD parser's accumulator.
    /// </summary>
    private sealed class InstructionAccumulator
    {
        private readonly List<ParsedInstructionSection> _sections = [];
        private readonly List<string> _pendingSteps = [];

        public void AddStep(string? step)
        {
            if (!string.IsNullOrEmpty(step))
            {
                _pendingSteps.Add(step);
            }
        }

        public void AddSection(string? title, IReadOnlyList<string> steps)
        {
            FlushPending();
            _sections.Add(new ParsedInstructionSection(title, steps.ToArray()));
        }

        public IReadOnlyList<ParsedInstructionSection> Build()
        {
            FlushPending();
            return _sections.ToArray();
        }

        private void FlushPending()
        {
            if (_pendingSteps.Count == 0)
            {
                return;
            }

            _sections.Add(new ParsedInstructionSection(null, _pendingSteps.ToArray()));
            _pendingSteps.Clear();
        }
    }
}
