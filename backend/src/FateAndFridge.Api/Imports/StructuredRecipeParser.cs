using System.Text.Json;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// Extracts schema.org <c>Recipe</c> nodes from <c>&lt;script
/// type="application/ld+json"&gt;</c> blocks (docs/M4_PLAN.md §1.5, §2.1;
/// docs/PRODUCT_SPEC.md §28). JSON-LD is tried before the HTML fallback.
/// </summary>
/// <remarks>
/// <para>
/// No JavaScript is executed and no browser is involved: the raw script text is
/// located, parsed as JSON, and walked. Malformed or truncated JSON-LD blocks
/// are skipped without throwing, so a page with one bad block and one good
/// block still yields the good recipe.
/// </para>
/// <para>
/// The walk covers the whole document, so top-level objects, arrays,
/// <c>@graph</c>, <c>mainEntity</c>, <c>itemListElement</c>, and arbitrarily
/// nested wrappers are all handled uniformly. <c>@type</c> may be a string or
/// an array and may be a bare name or a schema.org URL
/// (<c>https://schema.org/Recipe</c>), matched case-insensitively.
/// </para>
/// <para>
/// Every text value passes through <see cref="ImportTextSanitizer"/>; absent or
/// ambiguous fields stay null/empty rather than being guessed.
/// </para>
/// </remarks>
public static class StructuredRecipeParser
{
    private const string JsonLdMediaType = "application/ld+json";

    /// <summary>M3 maximum recipe duration, in minutes (docs/API.md §23).</summary>
    private const int MaxMinutes = 1440;

    /// <summary>Upper bound on visited JSON nodes; guards pathological documents.</summary>
    private const int MaxVisitedNodes = 200_000;

    /// <summary>Upper bound on recipes returned from a single document.</summary>
    private const int MaxRecipes = 100;

    /// <summary>Large-number guard so duration arithmetic cannot overflow.</summary>
    private const long DurationComponentCap = 1_000_000;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 256,
    };

    /// <summary>
    /// Parses every schema.org <c>Recipe</c> found in the page's JSON-LD blocks,
    /// in document order. Returns an empty list when the page has none.
    /// </summary>
    /// <param name="html">Raw page HTML.</param>
    /// <param name="pageUrl">
    /// URL the page was fetched from; used as the default <see cref="ParsedRecipe.SourceUrl"/>.
    /// </param>
    public static IReadOnlyList<ParsedRecipe> Parse(string? html, string? pageUrl = null)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        var sourceUrl = ImportTextSanitizer.Sanitize(pageUrl);
        var recipes = new List<ParsedRecipe>();

        foreach (var block in ExtractJsonLdBlocks(html))
        {
            ParseBlock(block, sourceUrl, recipes);
            if (recipes.Count >= MaxRecipes)
            {
                break;
            }
        }

        return recipes;
    }

    /// <summary>
    /// Attempts to parse an ISO 8601 duration (for example <c>PT1H30M</c>) into
    /// whole minutes. Returns <c>false</c> for null/blank/malformed input and for
    /// date components with ambiguous lengths (years, months). The result is
    /// non-negative and capped at 1440 minutes.
    /// </summary>
    public static bool TryParseIso8601Minutes(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Length < 2 || (text[0] != 'P' && text[0] != 'p'))
        {
            return false;
        }

        var index = 1;
        var inTime = false;
        var sawComponent = false;
        var sawTimeComponent = false;
        long totalMinutes = 0;

        while (index < text.Length)
        {
            if (text[index] is 'T' or 't')
            {
                if (inTime)
                {
                    return false;
                }

                inTime = true;
                index++;
                continue;
            }

            if (!char.IsAsciiDigit(text[index]))
            {
                return false;
            }

            var numberStart = index;
            while (index < text.Length && char.IsAsciiDigit(text[index]))
            {
                index++;
            }

            if (index >= text.Length || !long.TryParse(text.AsSpan(numberStart, index - numberStart), out var number))
            {
                return false;
            }

            var designator = text[index];
            index++;
            sawComponent = true;

            switch (designator)
            {
                case 'Y' or 'y':
                    // A year is not a fixed number of minutes.
                    return false;
                case 'M' when !inTime:
                    // A month is not a fixed number of minutes.
                    return false;
                case 'W' or 'w':
                    if (inTime)
                    {
                        return false;
                    }

                    totalMinutes = AddMinutes(totalMinutes, Capped(number) * 7 * 24 * 60);
                    break;
                case 'D' or 'd':
                    if (inTime)
                    {
                        return false;
                    }

                    totalMinutes = AddMinutes(totalMinutes, Capped(number) * 24 * 60);
                    break;
                case 'H' or 'h':
                    if (!inTime)
                    {
                        return false;
                    }

                    sawTimeComponent = true;
                    totalMinutes = AddMinutes(totalMinutes, Capped(number) * 60);
                    break;
                case 'M' or 'm':
                    if (!inTime)
                    {
                        return false;
                    }

                    sawTimeComponent = true;
                    totalMinutes = AddMinutes(totalMinutes, Capped(number));
                    break;
                case 'S' or 's':
                    if (!inTime)
                    {
                        return false;
                    }

                    sawTimeComponent = true;
                    totalMinutes = AddMinutes(totalMinutes, Capped(number) / 60);
                    break;
                default:
                    return false;
            }
        }

        if (!sawComponent || (inTime && !sawTimeComponent))
        {
            return false;
        }

        minutes = (int)Math.Min(totalMinutes, MaxMinutes);
        return true;
    }

    internal static IEnumerable<string> ExtractJsonLdBlocks(string html)
    {
        var searchIndex = 0;
        while (searchIndex < html.Length)
        {
            var open = IndexOfScriptOpen(html, searchIndex);
            if (open < 0)
            {
                yield break;
            }

            var tagEnd = FindTagEnd(html, open + "<script".Length);
            if (tagEnd < 0)
            {
                yield break;
            }

            var openTag = html[open..(tagEnd + 1)];
            var contentStart = tagEnd + 1;

            if (!IsJsonLdScript(openTag))
            {
                searchIndex = contentStart;
                continue;
            }

            var close = html.IndexOf("</script", contentStart, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
            {
                // Truncated page: there is no well-formed block to read.
                yield break;
            }

            yield return html[contentStart..close];
            searchIndex = close + "</script".Length;
        }
    }

    private static void ParseBlock(string block, string? sourceUrl, List<ParsedRecipe> recipes)
    {
        if (string.IsNullOrWhiteSpace(block))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(block, DocumentOptions);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            foreach (var recipe in FindRecipes(document.RootElement, sourceUrl))
            {
                recipes.Add(recipe);
                if (recipes.Count >= MaxRecipes)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Pre-order document walk that yields every node typed <c>Recipe</c>. A
    /// Recipe is not descended into, so a nested type reference cannot produce a
    /// duplicate candidate.
    /// </summary>
    private static IEnumerable<ParsedRecipe> FindRecipes(JsonElement root, string? sourceUrl)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        var visited = 0;

        while (stack.Count > 0)
        {
            var element = stack.Pop();
            visited++;
            if (visited > MaxVisitedNodes)
            {
                yield break;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (HasType(element, "Recipe"))
                    {
                        yield return MapRecipe(element, sourceUrl);
                        continue;
                    }

                    PushObjectChildren(element, stack);
                    break;
                case JsonValueKind.Array:
                    PushArrayChildren(element, stack);
                    break;
            }
        }
    }

    private static void PushObjectChildren(JsonElement element, Stack<JsonElement> stack)
    {
        var values = new List<JsonElement>();
        foreach (var property in element.EnumerateObject())
        {
            values.Add(property.Value);
        }

        for (var index = values.Count - 1; index >= 0; index--)
        {
            stack.Push(values[index]);
        }
    }

    private static void PushArrayChildren(JsonElement element, Stack<JsonElement> stack)
    {
        for (var index = element.GetArrayLength() - 1; index >= 0; index--)
        {
            stack.Push(element[index]);
        }
    }

    private static ParsedRecipe MapRecipe(JsonElement element, string? sourceUrl) => new()
    {
        Name = ImportTextSanitizer.Sanitize(ReadString(element, "name")),
        Description = ImportTextSanitizer.Sanitize(ReadString(element, "description")),
        Author = ReadAuthors(element),
        SourceUrl = sourceUrl,
        ImageUrl = ReadImageUrl(element),
        PrepMinutes = ReadDurationMinutes(element, "prepTime"),
        CookMinutes = ReadDurationMinutes(element, "cookTime"),
        IngredientLines = ReadIngredientLines(element),
        InstructionSections = ReadInstructionSections(element),
    };

    private static string? ReadAuthors(JsonElement recipe)
    {
        if (!recipe.TryGetProperty("author", out var author))
        {
            return null;
        }

        var names = new List<string>();
        CollectAuthorNames(author, names);
        if (names.Count == 0)
        {
            return null;
        }

        return ImportTextSanitizer.Sanitize(string.Join(", ", names));
    }

    private static void CollectAuthorNames(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = ImportTextSanitizer.Sanitize(element.GetString());
                if (!string.IsNullOrEmpty(value))
                {
                    names.Add(value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAuthorNames(item, names);
                }

                break;
            case JsonValueKind.Object:
                var name = ImportTextSanitizer.Sanitize(ReadString(element, "name"));
                if (!string.IsNullOrEmpty(name))
                {
                    names.Add(name);
                }

                break;
        }
    }

    private static IReadOnlyList<string> ReadIngredientLines(JsonElement recipe)
    {
        if (!recipe.TryGetProperty("recipeIngredient", out var list)
            && !recipe.TryGetProperty("ingredients", out list))
        {
            return [];
        }

        var lines = new List<string>();
        switch (list.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in list.EnumerateArray())
                {
                    var line = ReadTextLike(item);
                    if (!string.IsNullOrEmpty(line))
                    {
                        lines.Add(line);
                    }
                }

                break;
            case JsonValueKind.String:
                var single = ImportTextSanitizer.Sanitize(list.GetString());
                if (!string.IsNullOrEmpty(single))
                {
                    lines.Add(single);
                }

                break;
        }

        return lines;
    }

    private static IReadOnlyList<ParsedInstructionSection> ReadInstructionSections(JsonElement recipe)
    {
        if (!recipe.TryGetProperty("recipeInstructions", out var instructions))
        {
            return [];
        }

        var accumulator = new InstructionAccumulator();
        CollectInstructions(instructions, accumulator);
        return accumulator.Build();
    }

    private static void CollectInstructions(JsonElement element, InstructionAccumulator accumulator)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                accumulator.AddStep(ImportTextSanitizer.Sanitize(element.GetString()));
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectInstructions(item, accumulator);
                }

                break;
            case JsonValueKind.Object:
                if (HasType(element, "HowToSection"))
                {
                    CollectSection(element, accumulator);
                }
                else if (HasType(element, "HowToStep"))
                {
                    accumulator.AddStep(ReadStepText(element));
                }
                else if (element.TryGetProperty("itemListElement", out var list)
                    || element.TryGetProperty("steps", out list))
                {
                    CollectInstructions(list, accumulator);
                }
                else
                {
                    accumulator.AddStep(ReadStepText(element));
                }

                break;
        }
    }

    private static void CollectSection(JsonElement section, InstructionAccumulator accumulator)
    {
        var title = ImportTextSanitizer.Sanitize(ReadString(section, "name"));
        var steps = new List<string>();
        var nested = new List<JsonElement>();

        if (section.TryGetProperty("itemListElement", out var items)
            || section.TryGetProperty("steps", out items))
        {
            if (items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && HasType(item, "HowToSection"))
                    {
                        nested.Add(item);
                        continue;
                    }

                    var step = ReadStepText(item);
                    if (!string.IsNullOrEmpty(step))
                    {
                        steps.Add(step);
                    }
                }
            }
            else
            {
                var step = ReadStepText(items);
                if (!string.IsNullOrEmpty(step))
                {
                    steps.Add(step);
                }
            }
        }

        accumulator.AddSection(title, steps);
        foreach (var nestedSection in nested)
        {
            CollectSection(nestedSection, accumulator);
        }
    }

    private static string? ReadStepText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => ImportTextSanitizer.Sanitize(element.GetString()),
        JsonValueKind.Object => ImportTextSanitizer.Sanitize(
            ReadString(element, "text") ?? ReadString(element, "name")),
        _ => null,
    };

    private static string? ReadTextLike(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => ImportTextSanitizer.Sanitize(element.GetString()),
        JsonValueKind.Object => ImportTextSanitizer.Sanitize(
            ReadString(element, "name") ?? ReadString(element, "text")),
        _ => null,
    };

    private static string? ReadImageUrl(JsonElement recipe) =>
        recipe.TryGetProperty("image", out var image) ? ExtractImageUrl(image) : null;

    private static string? ExtractImageUrl(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return ImportTextSanitizer.Sanitize(element.GetString());
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var url = ExtractImageUrl(item);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return url;
                    }
                }

                return null;
            case JsonValueKind.Object:
                if (element.TryGetProperty("url", out var urlProperty))
                {
                    var url = ExtractImageUrl(urlProperty);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return url;
                    }
                }

                return ImportTextSanitizer.Sanitize(ReadString(element, "contentUrl"));
            default:
                return null;
        }
    }

    private static int? ReadDurationMinutes(JsonElement element, string propertyName)
    {
        var raw = ReadString(element, propertyName);
        return TryParseIso8601Minutes(raw, out var minutes) ? minutes : null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool HasType(JsonElement element, string typeName)
    {
        if (!element.TryGetProperty("@type", out var typeProperty))
        {
            return false;
        }

        switch (typeProperty.ValueKind)
        {
            case JsonValueKind.String:
                return TypeMatches(typeProperty.GetString(), typeName);
            case JsonValueKind.Array:
                foreach (var item in typeProperty.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && TypeMatches(item.GetString(), typeName))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Matches a bare schema.org type name or a schema.org URL form
    /// (<c>https://schema.org/Recipe</c>, <c>schema:Recipe</c>), ignoring case.
    /// </summary>
    private static bool TypeMatches(string? value, string typeName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var lastSeparator = Math.Max(
            Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('#')),
            trimmed.LastIndexOf(':'));
        var localName = lastSeparator >= 0 && lastSeparator < trimmed.Length - 1
            ? trimmed[(lastSeparator + 1)..]
            : trimmed;
        return string.Equals(localName, typeName, StringComparison.OrdinalIgnoreCase);
    }

    private static long Capped(long number) => number > DurationComponentCap ? DurationComponentCap : number;

    private static long AddMinutes(long total, long addend)
    {
        var sum = total + addend;
        return sum > MaxMinutes + 1 ? MaxMinutes + 1 : sum;
    }

    private static int IndexOfScriptOpen(string html, int start)
    {
        var index = start;
        while (index < html.Length)
        {
            index = html.IndexOf("<script", index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            var after = index + "<script".Length;
            if (after >= html.Length)
            {
                return -1;
            }

            var character = html[after];
            if (character == '>' || character == '/' || char.IsWhiteSpace(character))
            {
                return index;
            }

            index = after;
        }

        return -1;
    }

    private static int FindTagEnd(string html, int start)
    {
        char quote = '\0';
        for (var index = start; index < html.Length; index++)
        {
            var character = html[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                continue;
            }

            if (character == '>')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsJsonLdScript(string openTag)
    {
        var value = ReadAttributeValue(openTag, "type");
        if (value is null)
        {
            return false;
        }

        value = value.Trim();
        var separator = value.IndexOf(';');
        if (separator >= 0)
        {
            value = value[..separator].Trim();
        }

        return string.Equals(value, JsonLdMediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadAttributeValue(string tag, string name)
    {
        var index = 1; // skip '<'
        while (index < tag.Length && !char.IsWhiteSpace(tag[index]) && tag[index] != '>' && tag[index] != '/')
        {
            index++; // skip the element name
        }

        while (index < tag.Length)
        {
            while (index < tag.Length && (char.IsWhiteSpace(tag[index]) || tag[index] == '/'))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] == '>')
            {
                break;
            }

            var nameStart = index;
            while (index < tag.Length && !char.IsWhiteSpace(tag[index]) && tag[index] != '=' && tag[index] != '>' && tag[index] != '/')
            {
                index++;
            }

            var attributeName = tag[nameStart..index];
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] != '=')
            {
                if (string.Equals(attributeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                continue;
            }

            index++;
            while (index < tag.Length && char.IsWhiteSpace(tag[index]))
            {
                index++;
            }

            string attributeValue;
            if (index < tag.Length && (tag[index] == '"' || tag[index] == '\''))
            {
                var quote = tag[index];
                index++;
                var valueStart = index;
                while (index < tag.Length && tag[index] != quote)
                {
                    index++;
                }

                attributeValue = tag[valueStart..index];
                if (index < tag.Length)
                {
                    index++;
                }
            }
            else
            {
                var valueStart = index;
                while (index < tag.Length && !char.IsWhiteSpace(tag[index]) && tag[index] != '>')
                {
                    index++;
                }

                attributeValue = tag[valueStart..index];
            }

            if (string.Equals(attributeName, name, StringComparison.OrdinalIgnoreCase))
            {
                return attributeValue;
            }
        }

        return null;
    }

    /// <summary>
    /// Groups instruction steps and titled sections while preserving source
    /// order: consecutive bare steps share one untitled section, and each
    /// <c>HowToSection</c> becomes its own titled section.
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
