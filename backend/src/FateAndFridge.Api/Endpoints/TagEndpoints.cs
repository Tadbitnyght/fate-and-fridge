using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Tag as returned by the API (display form, docs/API.md §22).</summary>
public sealed record TagResponse(
    long Id,
    string Name)
{
    public static TagResponse From(TagRecord record) =>
        new(
            record.Id,
            record.Name);
}

/// <summary>
/// M2-10 tags routes (docs/API.md §22): authenticated reads, writes gated by
/// <c>reference_data.edit</c>, Admin-only hard delete with <c>TAG_IN_USE</c>
/// protection for referenced tags, and no retire concept.
/// </summary>
public static class TagEndpoints
{
    private const int DefaultLimit = 20;

    private const int MaxLimit = 100;

    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tags").WithTags("Tags");

        group.MapGet("", ListTagsAsync).RequireAuthenticated();
        group.MapPost("", CreateTagAsync).RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPatch("/{id:long}", UpdateTagAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapDelete("/{id:long}", DeleteTagAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> ListTagsAsync(
        HttpContext context,
        TagService tags,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var limit = DefaultLimit;
        if (context.Request.Query.TryGetValue("limit", out var limitValues))
        {
            var rawLimit = limitValues.Count > 0 ? limitValues[^1] : null;
            if (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1)
            {
                errors["limit"] = ["limit must be a positive integer."];
            }
            else
            {
                limit = Math.Min(parsed, MaxLimit);
            }
        }

        // Tags have no retired concept; the parameter is accepted for contract
        // uniformity and validated like the other reference-data lists.
        var includeRetired = false;
        if (context.Request.Query.TryGetValue("include_retired", out var retiredValues))
        {
            var rawRetired = retiredValues.Count > 0 ? retiredValues[^1] : null;
            if (!bool.TryParse(rawRetired, out includeRetired))
            {
                errors["include_retired"] = ["include_retired must be true or false."];
            }
        }

        string? normalizedSearch = null;
        if (context.Request.Query.TryGetValue("search", out var searchValues))
        {
            var rawSearch = searchValues.Count > 0 ? searchValues[^1] : null;
            if (!string.IsNullOrWhiteSpace(rawSearch))
            {
                if (ReferenceDataNormalizer.Normalize(rawSearch)
                    is ReferenceDataNormalizationResult.Success search)
                {
                    normalizedSearch = search.Normalized;
                }
                else
                {
                    errors["search"] = ["search contains characters that cannot be matched."];
                }
            }
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var items = await tags.ListAsync(
            normalizedSearch,
            limit,
            includeRetired,
            cancellationToken);
        return Results.Ok(items.Select(TagResponse.From).ToArray());
    }

    private static async Task<IResult> CreateTagAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        TagService tags,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? name = null;
        if (!root.TryGetProperty("name", out var nameElement))
        {
            errors["name"] = ["name is required and must be a string."];
        }
        else if (nameElement.ValueKind != JsonValueKind.String)
        {
            errors["name"] = ["name must be a string."];
        }
        else
        {
            name = nameElement.GetString();
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await tags.CreateAsync(name, ipAddress, actorUserId, cancellationToken);
        if (result is TagMutationResult.Success created)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.TagsEntity,
                actorUserId,
                created.Tag.Id,
                cancellationToken);
        }

        return result switch
        {
            TagMutationResult.Success success => Results.Json(
                TagResponse.From(success.Tag),
                statusCode: StatusCodes.Status201Created),
            TagMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            TagMutationResult.Conflict conflict => TagConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> UpdateTagAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        TagService tags,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var renameRequested = root.TryGetProperty("name", out var nameElement);
        string? name = null;
        if (renameRequested)
        {
            if (nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString();
            }
            else
            {
                errors["name"] = ["name must be a string."];
            }
        }
        else
        {
            errors["body"] = ["At least one of name must be provided."];
        }

        if (errors.Count > 0)
        {
            return ApiResults.ValidationError(errors);
        }

        var (actorUserId, ipAddress, actorError) = await ResolveActorAsync(
            context,
            actorProvider,
            cancellationToken);
        if (actorError is not null)
        {
            return actorError;
        }

        var result = await tags.UpdateAsync(
            id,
            name,
            renameRequested,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is TagMutationResult.Success updated)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.TagsEntity,
                actorUserId,
                updated.Tag.Id,
                cancellationToken);
        }

        return result switch
        {
            TagMutationResult.Success success => Results.Ok(
                TagResponse.From(success.Tag)),
            TagMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            TagMutationResult.Conflict conflict => TagConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> DeleteTagAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        TagService tags,
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

        var result = await tags.DeleteAsync(id, ipAddress, actorUserId, cancellationToken);
        if (result is TagMutationResult.Success)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.TagsEntity,
                actorUserId,
                id,
                cancellationToken);
        }

        return result switch
        {
            TagMutationResult.Success => Results.NoContent(),
            TagMutationResult.InUse inUse => TagInUse(inUse.UsageCount),
            _ => NotFound(),
        };
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

    private static IResult TagConflict(long? existingId)
    {
        var details = new Dictionary<string, object?>();
        if (existingId is not null)
        {
            details["existing_id"] = existingId.Value;
        }

        return ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.TagExists,
            "A tag with this name already exists.",
            details);
    }

    private static IResult TagInUse(int usageCount) =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.TagInUse,
            $"The tag is still referenced by {usageCount} recipe(s).",
            new Dictionary<string, object?>
            {
                ["usage_count"] = usageCount,
            });

    private static async Task<(JsonElement Root, IResult? Error)> ReadObjectAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var body = await JsonBodyReader.ReadAsync(context, cancellationToken: cancellationToken);
        if (!body.IsSuccess)
        {
            return (default, body.Error);
        }

        using var document = body.Document!;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (default, BodyError());
        }

        return (document.RootElement.Clone(), null);
    }

    private static IResult BodyError() =>
        ApiResults.ValidationError(new Dictionary<string, string[]>
        {
            ["body"] = ["The request body must be a valid JSON object."],
        });

    private static IResult NotFound() =>
        ApiResults.Error(
            StatusCodes.Status404NotFound,
            ErrorCodes.NotFound,
            "The tag does not exist.");
}
