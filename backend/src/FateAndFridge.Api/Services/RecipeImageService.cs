using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The single active image of a recipe as exposed to the API (M3-04,
/// docs/DATABASE.md §3). <see cref="StoragePath"/> stays server-side and is
/// never serialized by the response models.
/// </summary>
public sealed record RecipeImageRecord(
    long Id,
    string? AltText,
    string? SourceUrl,
    DateTimeOffset CreatedAt,
    string StoragePath);

/// <summary>Outcome of a recipe image write.</summary>
public abstract record RecipeImageMutationResult
{
    private RecipeImageMutationResult()
    {
    }

    public sealed record Success(RecipeImageRecord Image) : RecipeImageMutationResult;

    public sealed record NotFound : RecipeImageMutationResult;

    /// <summary>The bytes are not a supported image format (<c>415</c>).</summary>
    public sealed record TypeNotAllowed : RecipeImageMutationResult;

    /// <summary>Malformed, truncated, animated, or declared-type-mismatched content (<c>400 IMAGE_INVALID</c>).</summary>
    public sealed record InvalidImage : RecipeImageMutationResult;

    /// <summary>A supported image beyond the pixel-dimension limit (<c>400 IMAGE_DIMENSIONS_EXCEEDED</c>).</summary>
    public sealed record DimensionsExceeded : RecipeImageMutationResult;

    /// <summary>The uploaded bytes exceed the file-size cap (<c>413 IMAGE_TOO_LARGE</c>).</summary>
    public sealed record TooLarge : RecipeImageMutationResult;
}

/// <summary>Bytes of an active recipe image plus its detected content type.</summary>
public sealed record RecipeImageFile(RecipeImageRecord Image, byte[] Bytes, string ContentType);

/// <summary>
/// Local recipe image storage and content policy (M3-04, docs/M3_PLAN.md
/// §1.6–§1.7, docs/SECURITY.md §9): format detection from the bytes only,
/// bounded file size and dimensions, rejection of animation, random
/// server-generated filenames under <c>{DataDir}/images/recipes</c>, and an
/// audit row staged atomically with every change. Replaced and removed files
/// are deleted best-effort after the database commits.
/// </summary>
public sealed class RecipeImageService(
    IConfiguration configuration,
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<RecipeImageService> logger)
{
    /// <summary>Inclusive maximum uploaded file size: 5 MiB.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Inclusive maximum for each pixel dimension.</summary>
    public const int MaxDimension = 2048;

    /// <summary>Maximum alt-text length in characters.</summary>
    public const int MaxAltTextLength = 300;

    /// <summary>
    /// Hard cap for the whole multipart body: the file cap plus generous room
    /// for boundaries, headers, and alt text. The multipart reader rejects
    /// anything larger before it can be buffered.
    /// </summary>
    public const int MaxMultipartBodyBytes = MaxFileBytes + (64 * 1024);

    private string ImagesRoot => AppPaths.RecipeImagesRoot(configuration);

    /// <summary>
    /// Trims and validates alt text: null or empty clears it, other values must
    /// be at most <see cref="MaxAltTextLength"/> characters and contain no
    /// control characters. Returns null with <paramref name="error"/> set when
    /// invalid.
    /// </summary>
    public static string? NormalizeAltText(string? value, out string? error)
    {
        error = null;
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > MaxAltTextLength)
        {
            error = $"alt_text must be at most {MaxAltTextLength} characters.";
            return null;
        }

        foreach (var character in trimmed)
        {
            if (char.IsControl(character))
            {
                error = "alt_text contains unsupported control characters.";
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>Reads the active image row of a recipe, or null when absent.</summary>
    public async Task<RecipeImageRecord?> GetAsync(long recipeId, CancellationToken cancellationToken = default)
    {
        var image = await dbContext.RecipeImages.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.RecipeId == recipeId, cancellationToken);
        return image is null ? null : ToRecord(image);
    }

    /// <summary>
    /// Reads the served bytes and detected content type of a recipe's active
    /// image, or null when the recipe has no image or its file is unreadable.
    /// </summary>
    public async Task<RecipeImageFile?> ReadAsync(long recipeId, CancellationToken cancellationToken = default)
    {
        var image = await dbContext.RecipeImages.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.RecipeId == recipeId, cancellationToken);
        if (image is null)
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(image.StoragePath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "The image file for recipe {RecipeId} is unreadable; serving 404.",
                recipeId);
            return null;
        }

        var probe = RecipeImageFormatDetector.Probe(bytes);
        var contentType = probe.Format is { } format
            ? RecipeImageFormatDetector.ContentType(format)
            : "application/octet-stream";

        return new RecipeImageFile(ToRecord(image), bytes, contentType);
    }

    /// <summary>
    /// Validates and replaces (or creates) the active image. The new file is
    /// written and validated before any database change; a database failure
    /// deletes it again, and a successful replacement deletes the previous file
    /// best-effort. <paramref name="altText"/> must already be normalized by
    /// <see cref="NormalizeAltText"/>.
    /// </summary>
    public async Task<RecipeImageMutationResult> ReplaceAsync(
        long recipeId,
        byte[] content,
        string? declaredContentType,
        string? altText,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await dbContext.Recipes.AsNoTracking()
                .AnyAsync(recipe => recipe.Id == recipeId, cancellationToken))
        {
            return new RecipeImageMutationResult.NotFound();
        }

        if (content.LongLength > MaxFileBytes)
        {
            return new RecipeImageMutationResult.TooLarge();
        }

        var probe = RecipeImageFormatDetector.Probe(content);
        switch (probe.Outcome)
        {
            case RecipeImageProbeOutcome.UnknownFormat:
                return new RecipeImageMutationResult.TypeNotAllowed();
            case RecipeImageProbeOutcome.Malformed:
            case RecipeImageProbeOutcome.Animated:
                return new RecipeImageMutationResult.InvalidImage();
        }

        var format = probe.Format!.Value;
        if (!RecipeImageFormatDetector.IsDeclaredContentTypeCompatible(declaredContentType, format))
        {
            return new RecipeImageMutationResult.InvalidImage();
        }

        if (probe.Width > MaxDimension || probe.Height > MaxDimension)
        {
            return new RecipeImageMutationResult.DimensionsExceeded();
        }

        var replaced = await dbContext.RecipeImages
            .SingleOrDefaultAsync(image => image.RecipeId == recipeId, cancellationToken);
        var beforeJson = replaced is null ? null : SnapshotJson(replaced);
        var replacedPath = replaced?.StoragePath;

        var storagePath = await WriteNewFileAsync(content, format, cancellationToken);
        var image = new RecipeImage
        {
            RecipeId = recipeId,
            StoragePath = storagePath,
            AltText = altText,
            CreatedAt = timeProvider.GetUtcNow(),
        };

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (replaced is not null)
            {
                // Replacing removes the previous row so the new image gets a new
                // id and therefore a new URL/ETag; clients cannot keep a stale image.
                dbContext.RecipeImages.Remove(replaced);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            dbContext.RecipeImages.Add(image);
            await dbContext.SaveChangesAsync(cancellationToken);

            await auditService.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.ImageUpdated,
                    AuditEntityTypes.Recipe,
                    EntityId: recipeId.ToString(CultureInfo.InvariantCulture),
                    BeforeJson: beforeJson,
                    AfterJson: SnapshotJson(image),
                    IpAddress: ipAddress,
                    ActorUserId: actorUserId,
                    Metadata: new Dictionary<string, object?>
                    {
                        ["content_type"] = RecipeImageFormatDetector.ContentType(format),
                        ["byte_size"] = content.LongLength,
                        ["width"] = probe.Width,
                        ["height"] = probe.Height,
                    }),
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // The database change failed: the new file must not survive.
            TryDeleteFile(storagePath, "newly written");
            throw;
        }

        if (replacedPath is not null)
        {
            TryDeleteFile(replacedPath, "replaced");
        }

        logger.LogInformation(
            "Stored image {ImageId} ({ContentType}, {Width}x{Height}) for recipe {RecipeId}",
            image.Id,
            RecipeImageFormatDetector.ContentType(format),
            probe.Width,
            probe.Height,
            recipeId);

        return new RecipeImageMutationResult.Success(ToRecord(image));
    }

    /// <summary>
    /// Updates the alt text of the active image. <paramref name="altText"/> must
    /// already be normalized by <see cref="NormalizeAltText"/>; null clears it.
    /// </summary>
    public async Task<RecipeImageMutationResult> UpdateAltTextAsync(
        long recipeId,
        string? altText,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var image = await dbContext.RecipeImages
            .SingleOrDefaultAsync(candidate => candidate.RecipeId == recipeId, cancellationToken);
        if (image is null)
        {
            return new RecipeImageMutationResult.NotFound();
        }

        var beforeJson = SnapshotJson(image);
        image.AltText = altText;

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.ImageUpdated,
                AuditEntityTypes.Recipe,
                EntityId: recipeId.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: SnapshotJson(image),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Updated alt text of image {ImageId} for recipe {RecipeId}", image.Id, recipeId);
        return new RecipeImageMutationResult.Success(ToRecord(image));
    }

    /// <summary>
    /// Removes the active image: the row (and its audit row) is deleted first,
    /// then the file is removed best-effort.
    /// </summary>
    public async Task<RecipeImageMutationResult> RemoveAsync(
        long recipeId,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var image = await dbContext.RecipeImages
            .SingleOrDefaultAsync(candidate => candidate.RecipeId == recipeId, cancellationToken);
        if (image is null)
        {
            return new RecipeImageMutationResult.NotFound();
        }

        var beforeJson = SnapshotJson(image);
        dbContext.RecipeImages.Remove(image);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.ImageRemoved,
                AuditEntityTypes.Recipe,
                EntityId: recipeId.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        TryDeleteFile(image.StoragePath, "removed");

        logger.LogInformation("Removed image {ImageId} for recipe {RecipeId}", image.Id, recipeId);
        return new RecipeImageMutationResult.Success(ToRecord(image));
    }

    /// <summary>
    /// Writes the validated bytes to a fresh server-generated name under the
    /// image root. The name is 32 random hex characters plus the extension of
    /// the detected format; no user input influences the path.
    /// </summary>
    private async Task<string> WriteNewFileAsync(
        byte[] content,
        RecipeImageFormat format,
        CancellationToken cancellationToken)
    {
        var directory = ImagesRoot;
        Directory.CreateDirectory(directory);

        for (var attempt = 0; ; attempt++)
        {
            var fileName = string.Concat(
                RandomNumberGenerator.GetHexString(32, lowercase: true),
                RecipeImageFormatDetector.FileExtension(format));
            var path = Path.GetFullPath(Path.Combine(directory, fileName));

            try
            {
                await using var stream = new FileStream(
                    path,
                    new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                    });
                await stream.WriteAsync(content, cancellationToken);
                return path;
            }
            catch (IOException) when (attempt < 4)
            {
                // An astronomically unlikely generated-name collision: retry.
            }
        }
    }

    private void TryDeleteFile(string path, string operation)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Could not delete the {Operation} recipe image file at {Path}; it can be removed manually.",
                operation,
                path);
        }
    }

    private static string SnapshotJson(RecipeImage image) =>
        JsonSerializer.Serialize(new
        {
            id = image.Id,
            recipe_id = image.RecipeId,
            storage_path = image.StoragePath,
            source_url = image.SourceUrl,
            alt_text = image.AltText,
            created_at = image.CreatedAt,
        });

    private static RecipeImageRecord ToRecord(RecipeImage image) =>
        new(image.Id, image.AltText, image.SourceUrl, image.CreatedAt, image.StoragePath);
}
