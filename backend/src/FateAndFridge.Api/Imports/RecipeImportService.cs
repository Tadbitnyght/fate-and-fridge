using System.Text;

using FateAndFridge.Api.Data;
using FateAndFridge.Api.Services;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Imports;

/// <summary>Stable warning strings carried on an import draft (docs/M4_PLAN.md §2.2).</summary>
public static class ImportWarningCodes
{
    /// <summary>At least one ingredient line could not be matched to canonical reference data.</summary>
    public const string IngredientUnknown = "ingredient_unknown";
}

/// <summary>Outcome of <c>POST /api/v1/import/check</c> (docs/M4_PLAN.md §2.2).</summary>
public sealed record ImportCheckResult(
    ImportDraftReference? SourceUrlMatch,
    IReadOnlyList<ImportDraftReference> NameMatches);

/// <summary>Outcome of <c>POST /api/v1/import/recipe-url</c> before HTTP mapping.</summary>
public abstract record ImportRecipeUrlResult
{
    /// <summary>A draft was produced.</summary>
    public sealed record Success(ImportDraftResponse Draft) : ImportRecipeUrlResult;

    /// <summary>The safe fetch failed for a structured reason.</summary>
    public sealed record FetchFailed(ImportFetchFailureReason Reason) : ImportRecipeUrlResult;

    /// <summary>The page was fetched but no recipe could be parsed.</summary>
    public sealed record NoRecipe : ImportRecipeUrlResult;
}

/// <summary>
/// Orchestrates the draft-only import pipeline (docs/M4_PLAN.md §2.1–§2.2):
/// safe fetch, JSON-LD first with an HTML fallback, then draft assembly.
/// </summary>
/// <remarks>
/// <para>
/// This service performs <b>no database writes</b>. It reads existing recipes
/// only for duplicate/name diagnostics; saving a reviewed draft is the ordinary
/// <c>POST /api/v1/recipes</c> contract.
/// </para>
/// <para>
/// Ingredient lines are split into quantity/unit/name/preparation note and the
/// unit text is resolved against active reference data (M4-05). Canonical
/// ingredient matching (M4-06) still fills the remaining fields later, so
/// <c>matched_ingredient_id</c> stays null, ingredient <c>is_unknown</c> stays
/// true, and ingredient suggestions stay empty.
/// </para>
/// </remarks>
public sealed class RecipeImportService(
    AppDbContext dbContext,
    IImportFetcher fetcher,
    TimeProvider timeProvider,
    ILogger<RecipeImportService> logger)
{
    /// <summary>Maximum similar-name candidates returned per recipe or check.</summary>
    public const int MaxNameMatches = 5;

    /// <summary>Fetches and parses a page into a draft, or returns a structured failure.</summary>
    public async Task<ImportRecipeUrlResult> ImportAsync(
        string url,
        bool canCreateReferenceData,
        CancellationToken cancellationToken = default)
    {
        var fetched = await fetcher.FetchPageAsync(url, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess || fetched.FinalUrl is null)
        {
            return new ImportRecipeUrlResult.FetchFailed(fetched.FailureReason);
        }

        var finalUrl = fetched.FinalUrl;
        var html = Decode(fetched.Content);
        var (candidates, method) = ParseCandidates(html, finalUrl);
        if (candidates.Count == 0)
        {
            return new ImportRecipeUrlResult.NoRecipe();
        }

        var sourceUrl = finalUrl.AbsoluteUri;
        var sourceSite = finalUrl.Host;
        var sourceUrlMatch = await FindSourceUrlMatchAsync(finalUrl, sourceUrl, cancellationToken)
            .ConfigureAwait(false);
        var normalizer = new ImportNormalizer(
            await LoadUnitCatalogAsync(cancellationToken).ConfigureAwait(false));

        var recipes = new List<ImportDraftRecipe>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            recipes.Add(await BuildRecipeAsync(
                    candidates[index],
                    index,
                    sourceSite,
                    finalUrl,
                    normalizer,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        var draft = new ImportDraftResponse(
            new ImportDraftSource(
                sourceUrl,
                sourceSite,
                PageTitle: null,
                FirstAuthor(candidates),
                timeProvider.GetUtcNow(),
                method),
            sourceUrlMatch,
            new ImportDraftCapabilities(canCreateReferenceData),
            recipes);

        return new ImportRecipeUrlResult.Success(draft);
    }

    /// <summary>
    /// Re-runs the duplicate diagnostics after the user edits the draft
    /// (docs/M4_PLAN.md §2.2). M4-04 does exact normalized-URL and bounded
    /// normalized-name queries; M4-11/M4-12 add full scoring.
    /// </summary>
    public async Task<ImportCheckResult> CheckAsync(
        string? rawSourceUrl,
        string? name,
        CancellationToken cancellationToken = default)
    {
        ImportDraftReference? sourceUrlMatch = null;
        var trimmedUrl = rawSourceUrl?.Trim();
        if (!string.IsNullOrEmpty(trimmedUrl)
            && Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            sourceUrlMatch = await FindSourceUrlMatchAsync(uri, trimmedUrl, cancellationToken)
                .ConfigureAwait(false);
        }

        var nameMatches = await FindNameMatchesAsync(name, cancellationToken).ConfigureAwait(false);
        return new ImportCheckResult(sourceUrlMatch, nameMatches);
    }

    private (IReadOnlyList<ParsedRecipe> Candidates, string Method) ParseCandidates(string html, Uri finalUrl)
    {
        var pageUrl = finalUrl.AbsoluteUri;

        var jsonLd = StructuredRecipeParser.Parse(html, pageUrl);
        if (jsonLd.Count > 0)
        {
            return (jsonLd, "json_ld");
        }

        if (!HtmlDepthGuard.IsWithinDepthBudget(html))
        {
            logger.LogInformation(
                "Skipping HTML import fallback: tag nesting exceeds the safe depth of {MaxDepth}.",
                HtmlDepthGuard.MaxSafeTagDepth);
            return ([], "html");
        }

        return (HtmlFallbackParser.Parse(html, pageUrl), "html");
    }

    private async Task<ImportDraftRecipe> BuildRecipeAsync(
        ParsedRecipe candidate,
        int index,
        string sourceSite,
        Uri baseUri,
        ImportNormalizer normalizer,
        CancellationToken cancellationToken)
    {
        var name = Sanitize(candidate.Name, RecipeService.MaxNameLength) ?? string.Empty;
        var nameMatches = await FindNameMatchesAsync(name, cancellationToken).ConfigureAwait(false);

        var ingredients = new List<ImportDraftIngredient>(candidate.IngredientLines.Count);
        foreach (var line in candidate.IngredientLines)
        {
            var raw = Sanitize(line, RecipeService.MaxLongTextLength) ?? string.Empty;
            var parsed = normalizer.Parse(raw);
            ingredients.Add(new ImportDraftIngredient(
                Raw: raw,
                Quantity: ImportNormalizer.ClampQuantity(parsed.Quantity),
                Name: Sanitize(parsed.Name, RecipeService.MaxLongTextLength),
                PreparationNote: Sanitize(parsed.PreparationNote, RecipeService.MaxLongTextLength),
                MatchedIngredientId: null,
                MatchKind: null,
                IsUnknown: true,
                Suggestions: [],
                Unit: normalizer.ResolveUnit(parsed.UnitText)));
        }

        var warnings = ingredients.Count > 0
            ? new[] { ImportWarningCodes.IngredientUnknown }
            : [];

        var sections = candidate.InstructionSections
            .Select(section => new ImportDraftInstructionSection(
                Sanitize(section.Title, RecipeService.MaxLongTextLength),
                section.Steps
                    .Select(step => Sanitize(step, RecipeService.MaxLongTextLength) ?? string.Empty)
                    .ToArray()))
            .ToArray();

        return new ImportDraftRecipe(
            Index: index,
            Name: name,
            Sanitize(candidate.Description, RecipeService.MaxLongTextLength),
            Sanitize(candidate.Author, RecipeService.MaxAuthorLength),
            ResolveAbsolute(candidate.SourceUrl, baseUri),
            sourceSite,
            candidate.PrepMinutes,
            candidate.CookMinutes,
            Sanitize(candidate.Notes, RecipeService.MaxLongTextLength),
            ResolveAbsolute(candidate.ImageUrl, baseUri),
            nameMatches,
            ingredients,
            sections,
            Proteins: [],
            Tags: [],
            warnings);
    }

    /// <summary>Loads active units (and their aliases) for folded import matching (M4-05).</summary>
    private async Task<IReadOnlyList<ImportUnitCandidate>> LoadUnitCatalogAsync(CancellationToken cancellationToken)
    {
        var units = await dbContext.Units
            .AsNoTracking()
            .Where(unit => unit.IsActive)
            .Select(unit => new { unit.Id, unit.Symbol, unit.DisplayName, unit.Dimension })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (units.Count == 0)
        {
            return [];
        }

        var aliases = await dbContext.UnitAliases
            .AsNoTracking()
            .Where(alias => dbContext.Units.Any(unit => unit.Id == alias.UnitId && unit.IsActive))
            .Select(alias => new { alias.UnitId, alias.Alias })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var aliasesByUnit = aliases
            .GroupBy(alias => alias.UnitId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(alias => alias.Alias).ToArray());

        return units
            .Select(unit => new ImportUnitCandidate(
                unit.Id,
                unit.Symbol,
                unit.DisplayName,
                unit.Dimension,
                aliasesByUnit.TryGetValue(unit.Id, out var list) ? list : []))
            .ToArray();
    }

    /// <summary>Exact normalized-URL match against <c>recipes.normalized_source_url</c> (M4-11 extends this).</summary>
    private async Task<ImportDraftReference?> FindSourceUrlMatchAsync(
        Uri uri,
        string rawSourceUrl,
        CancellationToken cancellationToken)
    {
        var normalized = RecipeService.BuildNormalizedSourceUrl(uri, rawSourceUrl);
        return await dbContext.Recipes
            .AsNoTracking()
            .Where(recipe => recipe.NormalizedSourceUrl == normalized)
            .OrderBy(recipe => recipe.Id)
            .Select(recipe => new ImportDraftReference(recipe.Id, recipe.Name))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Bounded normalized-name containment query (M4-12 replaces this with scoring).</summary>
    private async Task<IReadOnlyList<ImportDraftReference>> FindNameMatchesAsync(
        string? name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        if (ReferenceDataNormalizer.Normalize(name) is not ReferenceDataNormalizationResult.Success success)
        {
            return [];
        }

        return await dbContext.Recipes
            .AsNoTracking()
            .Where(recipe => recipe.NormalizedName.Contains(success.Normalized))
            .OrderBy(recipe => recipe.Id)
            .Take(MaxNameMatches)
            .Select(recipe => new ImportDraftReference(recipe.Id, recipe.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? FirstAuthor(IReadOnlyList<ParsedRecipe> candidates)
    {
        foreach (var candidate in candidates)
        {
            var author = Sanitize(candidate.Author, RecipeService.MaxAuthorLength);
            if (author is not null)
            {
                return author;
            }
        }

        return null;
    }

    /// <summary>Resolves a possibly-relative URL to an absolute HTTP(S) URL, or null.</summary>
    private static string? ResolveAbsolute(string? value, Uri baseUri)
    {
        var sanitized = ImportTextSanitizer.Sanitize(value, RecipeService.MaxSourceUrlLength);
        if (sanitized is null)
        {
            return null;
        }

        if (!Uri.TryCreate(sanitized, UriKind.Absolute, out var absolute)
            && !Uri.TryCreate(baseUri, sanitized, out absolute))
        {
            return null;
        }

        if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
        {
            // On Unix, a leading-slash string parses as an absolute file: URI,
            // which would otherwise mask a same-site relative reference. Retry
            // the reference against the page URL before giving up.
            if (!Uri.TryCreate(baseUri, sanitized, out var resolved))
            {
                return null;
            }

            absolute = resolved;
            if (absolute.Scheme != Uri.UriSchemeHttp && absolute.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }
        }

        var result = absolute.AbsoluteUri;
        return result.Length > RecipeService.MaxSourceUrlLength ? null : result;
    }

    private static string Decode(byte[]? content) =>
        content is null || content.Length == 0 ? string.Empty : Encoding.UTF8.GetString(content);

    private static string? Sanitize(string? value, int maxLength) =>
        ImportTextSanitizer.Sanitize(value, maxLength);
}
