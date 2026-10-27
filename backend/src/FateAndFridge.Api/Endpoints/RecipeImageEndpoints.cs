using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace FateAndFridge.Api.Endpoints;

/// <summary>
/// The active recipe image as embedded in recipe documents (M3-04,
/// docs/M3_PLAN.md §1.6). <c>url</c> always points at the byte-serving route.
/// </summary>
public sealed record RecipeImageResponse(
    long Id,
    string? AltText,
    string? SourceUrl,
    DateTimeOffset CreatedAt,
    string Url)
{
    public static RecipeImageResponse From(long recipeId, RecipeImageRecord image) =>
        new(
            image.Id,
            image.AltText,
            image.SourceUrl,
            image.CreatedAt,
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/v1/recipes/{recipeId}/image"));
}

/// <summary>
/// M3-04 local recipe image routes (docs/M3_PLAN.md §1.6–§1.7,
/// docs/SECURITY.md §9): multipart upload/replacement, alt-text patch, removal,
/// and byte serving. Reads require <c>recipes.view</c>; writes require
/// <c>recipes.edit</c> and stage an audit row.
/// </summary>
public static class RecipeImageEndpoints
{
    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapRecipeImageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/recipes/{id:long}/image").WithTags("Recipes");

        group.MapPut("", ReplaceImageAsync).RequirePermission(PermissionKeys.RecipesEdit);
        group.MapPost("/from-url", DownloadImageFromUrlAsync).RequirePermission(PermissionKeys.RecipesEdit);
        group.MapPatch("", UpdateAltTextAsync).RequirePermission(PermissionKeys.RecipesEdit);
        group.MapDelete("", RemoveImageAsync).RequirePermission(PermissionKeys.RecipesEdit);
        group.MapGet("", GetImageAsync).RequirePermission(PermissionKeys.RecipesView);

        return app;
    }

    /// <summary>
    /// Uploads or replaces the active image from a bounded multipart form. The
    /// bytes are validated in memory before anything is written; unknown recipes
    /// are a 404.
    /// </summary>
    private static async Task<IResult> ReplaceImageAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RecipeImageService images,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            return ApiResults.Error(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.UnsupportedMediaType,
                "The request Content-Type must be multipart/form-data.");
        }

        if (context.Request.ContentLength is > 0 and var declaredLength
            && declaredLength > RecipeImageService.MaxMultipartBodyBytes)
        {
            return ImageTooLarge();
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ImageTooLarge();
        }
        catch (InvalidDataException exception)
        {
            // The multipart reader reports oversized bodies through a stable
            // "multipart body length limit" message; every other parse failure
            // is malformed input. Both are bounded before buffering.
            return exception.Message.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase)
                ? ImageTooLarge()
                : ImageInvalid();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var file = form.Files["file"];
        if (file is null)
        {
            errors["file"] = ["file is required and must be an uploaded image."];
        }

        var altText = ReadAltText(form["alt_text"], errors);
        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        if (file!.Length > RecipeImageService.MaxFileBytes)
        {
            return ImageTooLarge();
        }

        byte[] content;
        try
        {
            content = new byte[file.Length];
            await using var stream = file.OpenReadStream();
            var read = await stream.ReadAtLeastAsync(
                content,
                content.Length,
                throwOnEndOfStream: false,
                cancellationToken);
            if (read != content.Length)
            {
                return ImageInvalid();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return ImageInvalid();
        }

        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await images.ReplaceAsync(
            id,
            content,
            file.ContentType,
            altText,
            sourceUrl: null,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is RecipeImageMutationResult.Success)
        {
            await liveSync.RecipeUpdatedAsync(actorUserId, id, cancellationToken);
        }

        return result switch
        {
            RecipeImageMutationResult.Success success =>
                Results.Ok(RecipeImageResponse.From(id, success.Image)),
            RecipeImageMutationResult.NotFound => NotFound(),
            RecipeImageMutationResult.TypeNotAllowed => ApiResults.Error(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.ImageTypeNotAllowed,
                "The image must be a JPEG, PNG, or WebP."),
            RecipeImageMutationResult.DimensionsExceeded => ApiResults.Error(
                StatusCodes.Status400BadRequest,
                ErrorCodes.ImageDimensionsExceeded,
                $"The image dimensions must not exceed {RecipeImageService.MaxDimension} pixels."),
            RecipeImageMutationResult.TooLarge => ImageTooLarge(),
            _ => ImageInvalid(),
        };
    }

    /// <summary>
    /// Downloads an image from a user-supplied URL and replaces the active
    /// image (M4-07, docs/M4_PLAN.md §1.8/§2.2): SSRF-hardened fetch, M3 content
    /// policy on the actual bytes, per-actor image-fetch budget, and a single
    /// atomic replacement. A failed download never modifies the existing image.
    /// </summary>
    private static async Task<IResult> DownloadImageFromUrlAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RecipeImageImportService imageImport,
        IImportThrottle throttle,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        using var document = body.Document!;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BodyError();
        }

        if (!root.TryGetProperty("source_url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(urlElement.GetString()))
        {
            return ValidationError(
                "source_url",
                "source_url is required and must be a non-blank string.");
        }

        var sourceUrl = urlElement.GetString()!.Trim();

        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            return ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage);
        }

        // A 404 on an unknown recipe must not consume an image-fetch slot and
        // must not perform any outbound request: the id is caller-chosen, so a
        // hostile actor could otherwise exhaust a victim's 60/hour budget with
        // requests for nonexistent recipes.
        if (!await imageImport.RecipeExistsAsync(id, cancellationToken))
        {
            return NotFound();
        }

        var actorKey = actor.IsSystem
            ? "system"
            : actor.UserId?.ToString(CultureInfo.InvariantCulture) ?? "system";

        var decision = throttle.Reserve(actorKey, ImportFetchBudget.Image);
        if (!decision.Allowed)
        {
            context.Response.Headers.RetryAfter = Math
                .Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            return ApiResults.Error(
                StatusCodes.Status429TooManyRequests,
                ErrorCodes.ImportRateLimited,
                "Too many import fetches; try again later.");
        }

        var ipAddress = context.Connection.RemoteIpAddress?.ToString();
        var actorUserId = actor.IsSystem ? null : actor.UserId;

        var result = await imageImport.DownloadAsync(
            id,
            sourceUrl,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is ImportImageResult.Success)
        {
            await liveSync.RecipeUpdatedAsync(actorUserId, id, cancellationToken);
        }

        return result switch
        {
            ImportImageResult.Success success =>
                Results.Ok(RecipeImageResponse.From(id, success.Image)),
            ImportImageResult.NotFound => NotFound(),
            ImportImageResult.InvalidUrl => ApiResults.Error(
                StatusCodes.Status400BadRequest,
                ErrorCodes.ImportUrlInvalid,
                "The URL is not a valid http or https URL."),
            ImportImageResult.BlockedTarget => ApiResults.Error(
                StatusCodes.Status422UnprocessableEntity,
                ErrorCodes.ImportUrlBlocked,
                "The URL targets a blocked address."),
            ImportImageResult.TooLarge => ApiResults.Error(
                StatusCodes.Status413PayloadTooLarge,
                ErrorCodes.ImportTooLarge,
                "The image exceeds the import size limit."),
            ImportImageResult.UnsupportedContent => ApiResults.Error(
                StatusCodes.Status415UnsupportedMediaType,
                ErrorCodes.ImportUnsupportedContent,
                "The URL did not return a JPEG, PNG, or WebP image within the size and dimension limits."),
            _ => ApiResults.Error(
                StatusCodes.Status502BadGateway,
                ErrorCodes.ImportFetchFailed,
                "The image could not be fetched."),
        };
    }


    /// <summary>Updates the alt text; null or empty clears it.</summary>
    private static async Task<IResult> UpdateAltTextAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RecipeImageService images,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return body.Error!;
        }

        using var document = body.Document!;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return BodyError();
        }

        if (!root.TryGetProperty("alt_text", out var element))
        {
            return ValidationError(
                "alt_text",
                "alt_text is required and must be a string or null.");
        }

        string? raw;
        if (element.ValueKind == JsonValueKind.String)
        {
            raw = element.GetString();
        }
        else if (element.ValueKind == JsonValueKind.Null)
        {
            raw = null;
        }
        else
        {
            return ValidationError("alt_text", "alt_text must be a string or null.");
        }

        var altText = RecipeImageService.NormalizeAltText(raw, out var altTextError);
        if (altTextError is not null)
        {
            return ValidationError("alt_text", altTextError);
        }

        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await images.UpdateAltTextAsync(
            id,
            altText,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is RecipeImageMutationResult.Success)
        {
            await liveSync.RecipeUpdatedAsync(actorUserId, id, cancellationToken);
        }

        return result switch
        {
            RecipeImageMutationResult.Success success =>
                Results.Ok(RecipeImageResponse.From(id, success.Image)),
            RecipeImageMutationResult.NotFound => NotFound(),
            _ => ImageInvalid(),
        };
    }

    /// <summary>Removes the active image; the row commits first, the file follows best-effort.</summary>
    private static async Task<IResult> RemoveImageAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        RecipeImageService images,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await images.RemoveAsync(id, ipAddress, actorUserId, cancellationToken);
        if (result is RecipeImageMutationResult.Success)
        {
            await liveSync.RecipeUpdatedAsync(actorUserId, id, cancellationToken);
        }

        return result is RecipeImageMutationResult.Success ? Results.NoContent() : NotFound();
    }

    /// <summary>
    /// Serves the active image with the content type detected at upload time,
    /// an id-derived ETag, and <c>Cache-Control: private, no-cache</c>;
    /// If-None-Match is answered with a 304.
    /// </summary>
    private static async Task<IResult> GetImageAsync(
        long id,
        HttpContext context,
        RecipeImageService images,
        CancellationToken cancellationToken)
    {
        var file = await images.ReadAsync(id, cancellationToken);
        if (file is null)
        {
            return NotFound();
        }

        var entityTag = new EntityTagHeaderValue($"\"{file.Image.Id}\"");
        context.Response.Headers.ETag = entityTag.ToString();
        context.Response.Headers.CacheControl = "private, no-cache";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        foreach (var value in context.Request.Headers.IfNoneMatch)
        {
            if (EntityTagHeaderValue.TryParse(value, out var candidate)
                && candidate.Compare(entityTag, true))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }

        return Results.Bytes(file.Bytes, file.ContentType);
    }

    private static string? ReadAltText(
        StringValues values,
        Dictionary<string, string[]> errors)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var altText = RecipeImageService.NormalizeAltText(values[^1], out var error);
        if (error is not null)
        {
            errors["alt_text"] = [error];
            return null;
        }

        return altText;
    }

    private static async Task<(long? ActorUserId, string? IpAddress, IResult? Error)> ResolveActorAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        CancellationToken cancellationToken)
    {
        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            // The permission filter already rejected unauthenticated requests;
            // keep the defensive envelope so the handler cannot fail open.
            return (null, null, ApiResults.Error(
                StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthRequired,
                AuthRequiredMessage));
        }

        return (
            actor.IsSystem ? null : actor.UserId,
            context.Connection.RemoteIpAddress?.ToString(),
            null);
    }

    private static IResult ValidationError(string field, string message) =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            [field] = [message],
        });

    private static IResult BodyError() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });

    private static IResult ImageTypeNotAllowed() => ApiResults.Error(
        StatusCodes.Status415UnsupportedMediaType,
        ErrorCodes.ImageTypeNotAllowed,
        "The image must be a JPEG, PNG, or WebP.");

    private static IResult ImageInvalid() => ApiResults.Error(
        StatusCodes.Status400BadRequest,
        ErrorCodes.ImageInvalid,
        "The uploaded image is malformed, animated, or does not match its declared content type.");

    private static IResult ImageTooLarge() => ApiResults.Error(
        StatusCodes.Status413PayloadTooLarge,
        ErrorCodes.ImageTooLarge,
        $"The image must be at most {RecipeImageService.MaxFileBytes / (1024 * 1024)} MB.");

    private static IResult NotFound() => ApiResults.Error(
        StatusCodes.Status404NotFound,
        ErrorCodes.NotFound,
        "The recipe does not exist or has no image.");
}
