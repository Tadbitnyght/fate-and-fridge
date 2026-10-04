using FateAndFridge.Api.Data;
using FateAndFridge.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Imports;

/// <summary>
/// Outcome of <c>POST /api/v1/recipes/{id}/image/from-url</c> before HTTP
/// mapping (docs/M4_PLAN.md §1.8/§2.2).
/// </summary>
public abstract record ImportImageResult
{
    /// <summary>The image was downloaded, validated, and became the active image.</summary>
    public sealed record Success(RecipeImageRecord Image) : ImportImageResult;

    /// <summary>The recipe does not exist (<c>404 NOT_FOUND</c>).</summary>
    public sealed record NotFound : ImportImageResult;

    /// <summary>The URL is malformed or not HTTP/HTTPS (<c>400 IMPORT_URL_INVALID</c>).</summary>
    public sealed record InvalidUrl : ImportImageResult;

    /// <summary>The URL host/address is a blocked SSRF target (<c>422 IMPORT_URL_BLOCKED</c>).</summary>
    public sealed record BlockedTarget : ImportImageResult;

    /// <summary>DNS, timeout, transport, redirect, or non-success status (<c>502 IMPORT_FETCH_FAILED</c>).</summary>
    public sealed record FetchFailed : ImportImageResult;

    /// <summary>Response body exceeds the image ceiling (<c>413 IMPORT_TOO_LARGE</c>).</summary>
    public sealed record TooLarge : ImportImageResult;

    /// <summary>
    /// Declared content type, detected signature, animation, or dimensions fail
    /// the M3 image policy (<c>415 IMPORT_UNSUPPORTED_CONTENT</c>).
    /// </summary>
    public sealed record UnsupportedContent : ImportImageResult;
}

/// <summary>
/// Downloads a recipe image from a user-supplied URL and stores it through the
/// M3 image path (docs/M4_PLAN.md §1.8/§2.2, docs/SECURITY.md §6–§9):
/// the SSRF-hardened <see cref="IImportFetcher"/> enforces URL policy, redirects,
/// timeouts, and the 5 MiB image ceiling; the bytes must then pass the M3
/// format/signature/dimension policy before anything is written. A failed
/// download never touches the existing image, and a successful replacement is
/// atomic (new file written first, database swap in one transaction, old file
/// deleted best-effort afterwards).
/// </summary>
public sealed class RecipeImageImportService(
    AppDbContext dbContext,
    IImportFetcher fetcher,
    RecipeImageService images,
    ILogger<RecipeImageImportService> logger)
{
    /// <summary>
    /// Safe-fetches <paramref name="sourceUrl"/> as an image, validates it, and
    /// replaces the recipe's active image. Missing recipes are reported before
    /// any outbound request is made.
    /// </summary>
    public async Task<ImportImageResult> DownloadAsync(
        long recipeId,
        string sourceUrl,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Recipes.AsNoTracking()
                .AnyAsync(recipe => recipe.Id == recipeId, cancellationToken)
                .ConfigureAwait(false))
        {
            return new ImportImageResult.NotFound();
        }

        var fetched = await fetcher.FetchImageAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
        if (!fetched.IsSuccess || fetched.Content is null || fetched.FinalUrl is null)
        {
            return MapFetchFailure(fetched.FailureReason);
        }

        var content = fetched.Content;
        var declaredContentType = fetched.ContentType;

        // The M3 policy is authoritative: the fetcher only checked the declared
        // media type, so the actual bytes must pass signature/dimension checks
        // and match the declared type before storage.
        var probe = RecipeImageFormatDetector.Probe(content);
        switch (probe.Outcome)
        {
            case RecipeImageProbeOutcome.UnknownFormat:
            case RecipeImageProbeOutcome.Malformed:
            case RecipeImageProbeOutcome.Animated:
                return new ImportImageResult.UnsupportedContent();
        }

        var format = probe.Format!.Value;
        if (!RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(declaredContentType, format)
            || probe.Width > RecipeImageService.MaxDimension
            || probe.Height > RecipeImageService.MaxDimension)
        {
            return new ImportImageResult.UnsupportedContent();
        }

        // Record the final (post-redirect) URL as the honest source, bounded to
        // the recipe source-url ceiling. A longer value is dropped, not guessed.
        string? resolvedSourceUrl = fetched.FinalUrl.AbsoluteUri;
        if (resolvedSourceUrl.Length > RecipeService.MaxSourceUrlLength)
        {
            resolvedSourceUrl = null;
        }

        var result = await images.ReplaceAsync(
            recipeId,
            content,
            declaredContentType,
            altText: null,
            resolvedSourceUrl,
            ipAddress,
            actorUserId,
            cancellationToken).ConfigureAwait(false);

        if (result is RecipeImageMutationResult.Success stored)
        {
            logger.LogInformation(
                "Imported image {ImageId} for recipe {RecipeId} from {SourceUrl}",
                stored.Image.Id,
                recipeId,
                resolvedSourceUrl);
        }

        return result switch
        {
            RecipeImageMutationResult.Success success => new ImportImageResult.Success(success.Image),
            RecipeImageMutationResult.NotFound => new ImportImageResult.NotFound(),
            RecipeImageMutationResult.TooLarge => new ImportImageResult.TooLarge(),
            _ => new ImportImageResult.UnsupportedContent(),
        };
    }

    private ImportImageResult MapFetchFailure(ImportFetchFailureReason reason) => reason switch
    {
        ImportFetchFailureReason.InvalidUrl => new ImportImageResult.InvalidUrl(),
        ImportFetchFailureReason.BlockedTarget => new ImportImageResult.BlockedTarget(),
        ImportFetchFailureReason.TooLarge => new ImportImageResult.TooLarge(),
        ImportFetchFailureReason.UnsupportedContentType => new ImportImageResult.UnsupportedContent(),
        _ => new ImportImageResult.FetchFailed(),
    };
}
