using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Protein as returned by the API (display form, docs/API.md §22).</summary>
public sealed record ProteinResponse(
    long Id,
    string Name,
    bool IsActive)
{
    public static ProteinResponse From(ProteinRecord record) =>
        new(
            record.Id,
            record.Name,
            record.IsActive);
}

/// <summary>
/// M2-10 proteins routes (docs/API.md §22): authenticated reads, writes gated
/// by <c>reference_data.edit</c>, Admin-only hard delete, and retire semantics
/// with <c>PROTEIN_IN_USE</c> protection for referenced proteins.
/// </summary>
public static class ProteinEndpoints
{
    private const int DefaultLimit = 20;

    private const int MaxLimit = 100;

    private const string AuthRequiredMessage = "Authentication is required.";

    public static IEndpointRouteBuilder MapProteinEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/proteins").WithTags("Proteins");

        group.MapGet("", ListProteinsAsync).RequireAuthenticated();
        group.MapPost("", CreateProteinAsync).RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPatch("/{id:long}", UpdateProteinAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPost("/{id:long}/retire", RetireProteinAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapDelete("/{id:long}", DeleteProteinAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> ListProteinsAsync(
        HttpContext context,
        ProteinService proteins,
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

        var items = await proteins.ListAsync(
            normalizedSearch,
            limit,
            includeRetired,
            cancellationToken);
        return Results.Ok(items.Select(ProteinResponse.From).ToArray());
    }

    private static async Task<IResult> CreateProteinAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        ProteinService proteins,
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

        var result = await proteins.CreateAsync(name, ipAddress, actorUserId, cancellationToken);
        if (result is ProteinMutationResult.Success created)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.ProteinsEntity,
                actorUserId,
                created.Protein.Id,
                cancellationToken);
        }

        return result switch
        {
            ProteinMutationResult.Success success => Results.Json(
                ProteinResponse.From(success.Protein),
                statusCode: StatusCodes.Status201Created),
            ProteinMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            ProteinMutationResult.Conflict conflict => ProteinConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> UpdateProteinAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        ProteinService proteins,
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

        var activeRequested = root.TryGetProperty("is_active", out var activeElement);
        bool? isActive = null;
        if (activeRequested)
        {
            if (activeElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isActive = activeElement.GetBoolean();
            }
            else
            {
                errors["is_active"] = ["is_active must be true or false."];
            }
        }

        if (!renameRequested && !activeRequested)
        {
            errors["body"] = ["At least one of name or is_active must be provided."];
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

        var result = await proteins.UpdateAsync(
            id,
            name,
            renameRequested,
            isActive,
            activeRequested,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is ProteinMutationResult.Success updated)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.ProteinsEntity,
                actorUserId,
                updated.Protein.Id,
                cancellationToken);
        }

        return result switch
        {
            ProteinMutationResult.Success success => Results.Ok(
                ProteinResponse.From(success.Protein)),
            ProteinMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            ProteinMutationResult.Conflict conflict => ProteinConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> RetireProteinAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        ProteinService proteins,
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

        var result = await proteins.RetireAsync(id, ipAddress, actorUserId, cancellationToken);
        if (result is ProteinMutationResult.Success retired)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.ProteinsEntity,
                actorUserId,
                retired.Protein.Id,
                cancellationToken);
        }

        return result switch
        {
            ProteinMutationResult.Success success => Results.Ok(
                ProteinResponse.From(success.Protein)),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> DeleteProteinAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        ProteinService proteins,
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

        var result = await proteins.DeleteAsync(id, ipAddress, actorUserId, cancellationToken);
        if (result is ProteinMutationResult.Success)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.ProteinsEntity,
                actorUserId,
                id,
                cancellationToken);
        }

        return result switch
        {
            ProteinMutationResult.Success => Results.NoContent(),
            ProteinMutationResult.InUse inUse => ProteinInUse(inUse.UsageCount),
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

    private static IResult ProteinConflict(long? existingId)
    {
        var details = new Dictionary<string, object?>();
        if (existingId is not null)
        {
            details["existing_id"] = existingId.Value;
        }

        return ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.ProteinExists,
            "A protein with this name already exists.",
            details);
    }

    private static IResult ProteinInUse(int usageCount) =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.ProteinInUse,
            $"The protein is still referenced by {usageCount} recipe(s); retire it instead.",
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
            "The protein does not exist.");
}
