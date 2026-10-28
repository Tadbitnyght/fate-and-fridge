using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Endpoints;

/// <summary>Unit alias as returned by the API (display form, docs/API.md §22).</summary>
public sealed record UnitAliasResponse(long Id, string Alias)
{
    public static UnitAliasResponse From(UnitAliasRecord record) =>
        new(record.Id, record.Alias);
}

/// <summary>Unit as returned by the API (display form, docs/API.md §22).</summary>
public sealed record UnitResponse(
    long Id,
    string Symbol,
    string DisplayName,
    string Dimension,
    decimal? BaseFactor,
    bool IsCustom,
    bool IsActive,
    IReadOnlyList<UnitAliasResponse> Aliases)
{
    public static UnitResponse From(UnitRecord record) =>
        new(
            record.Id,
            record.Symbol,
            record.DisplayName,
            EnumTextConverters.UnitDimensionToText(record.Dimension),
            record.BaseFactor,
            record.IsCustom,
            record.IsActive,
            record.Aliases.Select(UnitAliasResponse.From).ToArray());
}

/// <summary>
/// Units routes (docs/API.md §22): authenticated reads, writes gated by
/// <c>reference_data.edit</c>, Admin-only hard delete, per-unit aliases, and the
/// immutable-dimension rule.
/// </summary>
public static class UnitEndpoints
{
    private const int DefaultLimit = 20;

    private const int MaxLimit = 100;

    private const string AuthRequiredMessage = "Authentication is required.";

    private const string DimensionMessage =
        "dimension must be one of mass, volume, count, nonconvertible.";

    public static IEndpointRouteBuilder MapUnitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/units").WithTags("Units");

        group.MapGet("", ListUnitsAsync).RequireAuthenticated();
        group.MapPost("", CreateUnitAsync).RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPatch("/{id:long}", UpdateUnitAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPost("/{id:long}/aliases", AddAliasAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapDelete("/{id:long}/aliases/{aliasId:long}", RemoveAliasAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapPost("/{id:long}/retire", RetireUnitAsync)
            .RequirePermission(PermissionKeys.ReferenceDataEdit);
        group.MapDelete("/{id:long}", DeleteUnitAsync).RequireAdmin();

        return app;
    }

    private static async Task<IResult> ListUnitsAsync(
        HttpContext context,
        UnitService units,
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

        var items = await units.ListAsync(normalizedSearch, limit, includeRetired, cancellationToken);
        return Results.Ok(items.Select(UnitResponse.From).ToArray());
    }

    private static async Task<IResult> CreateUnitAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        UnitService units,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        string? symbol = null;
        if (!root.TryGetProperty("symbol", out var symbolElement))
        {
            errors["symbol"] = ["symbol is required and must be a string."];
        }
        else if (symbolElement.ValueKind != JsonValueKind.String)
        {
            errors["symbol"] = ["symbol must be a string."];
        }
        else
        {
            symbol = symbolElement.GetString();
        }

        string? displayName = null;
        if (!root.TryGetProperty("display_name", out var displayElement))
        {
            errors["display_name"] = ["display_name is required and must be a string."];
        }
        else if (displayElement.ValueKind != JsonValueKind.String)
        {
            errors["display_name"] = ["display_name must be a string."];
        }
        else
        {
            displayName = displayElement.GetString();
        }

        UnitDimension? dimension = null;
        if (!root.TryGetProperty("dimension", out var dimensionElement))
        {
            errors["dimension"] = [DimensionMessage];
        }
        else if (dimensionElement.ValueKind != JsonValueKind.String
            || !EnumTextConverters.TryUnitDimensionFromText(dimensionElement.GetString(), out var parsedDimension))
        {
            errors["dimension"] = [DimensionMessage];
        }
        else
        {
            dimension = parsedDimension;
        }

        var factorRequested = TryReadFactor(root, errors, out var baseFactor);

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

        var result = await units.CreateAsync(
            symbol,
            displayName,
            dimension!.Value,
            baseFactor,
            factorRequested,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is UnitMutationResult.Success created)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.UnitsEntity,
                actorUserId,
                created.Unit.Id,
                cancellationToken);
        }

        return result switch
        {
            UnitMutationResult.Success success => Results.Json(
                UnitResponse.From(success.Unit),
                statusCode: StatusCodes.Status201Created),
            UnitMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            UnitMutationResult.Conflict conflict => UnitConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> UpdateUnitAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        UnitService units,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        // A unit's dimension is immutable (docs/DATABASE.md §5); carrying the
        // field at all is the attempt, even with the current value.
        if (root.TryGetProperty("dimension", out _))
        {
            return ApiResults.Error(
                StatusCodes.Status400BadRequest,
                ErrorCodes.InvalidUnitDimensionChange,
                "A unit's dimension is immutable after creation; retire the unit and create a new one instead.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var symbolRequested = root.TryGetProperty("symbol", out var symbolElement);
        string? symbol = null;
        if (symbolRequested)
        {
            if (symbolElement.ValueKind == JsonValueKind.String)
            {
                symbol = symbolElement.GetString();
            }
            else
            {
                errors["symbol"] = ["symbol must be a string."];
            }
        }

        var displayRequested = root.TryGetProperty("display_name", out var displayElement);
        string? displayName = null;
        if (displayRequested)
        {
            if (displayElement.ValueKind == JsonValueKind.String)
            {
                displayName = displayElement.GetString();
            }
            else
            {
                errors["display_name"] = ["display_name must be a string."];
            }
        }

        var factorRequested = TryReadFactor(root, errors, out var baseFactor);

        if (!symbolRequested && !displayRequested && !factorRequested)
        {
            errors["body"] = ["At least one of symbol, display_name, or base_factor must be provided."];
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

        var result = await units.UpdateAsync(
            id,
            symbol,
            symbolRequested,
            displayName,
            displayRequested,
            baseFactor,
            factorRequested,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is UnitMutationResult.Success updated)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.UnitsEntity,
                actorUserId,
                updated.Unit.Id,
                cancellationToken);
        }

        return result switch
        {
            UnitMutationResult.Success success => Results.Ok(UnitResponse.From(success.Unit)),
            UnitMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            UnitMutationResult.Conflict conflict => UnitConflict(conflict.ExistingId),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> AddAliasAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        UnitService units,
        ILiveSyncPublisher liveSync,
        CancellationToken cancellationToken)
    {
        var (root, error) = await ReadObjectAsync(context, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string? alias = null;
        if (!root.TryGetProperty("alias", out var aliasElement))
        {
            errors["alias"] = ["alias is required and must be a string."];
        }
        else if (aliasElement.ValueKind != JsonValueKind.String)
        {
            errors["alias"] = ["alias must be a string."];
        }
        else
        {
            alias = aliasElement.GetString();
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

        var result = await units.AddAliasAsync(
            id,
            alias,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is UnitAliasMutationResult.Success)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.UnitsEntity,
                actorUserId,
                id,
                cancellationToken);
        }

        return result switch
        {
            UnitAliasMutationResult.Success success => Results.Json(
                UnitAliasResponse.From(success.Alias),
                statusCode: StatusCodes.Status201Created),
            UnitAliasMutationResult.ValidationFailed failed => ApiResults.ValidationError(failed.FieldErrors),
            UnitAliasMutationResult.AliasConflict conflict => AliasConflict(conflict.Details),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> RemoveAliasAsync(
        long id,
        long aliasId,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        UnitService units,
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

        var result = await units.RemoveAliasAsync(
            id,
            aliasId,
            ipAddress,
            actorUserId,
            cancellationToken);
        if (result is UnitAliasMutationResult.Success)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.UnitsEntity,
                actorUserId,
                id,
                cancellationToken);
        }

        return result switch
        {
            UnitAliasMutationResult.Success => Results.NoContent(),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> RetireUnitAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        UnitService units,
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

        var result = await units.RetireAsync(id, ipAddress, actorUserId, cancellationToken);
        if (result is UnitMutationResult.Success retired)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.UnitsEntity,
                actorUserId,
                retired.Unit.Id,
                cancellationToken);
        }

        return result switch
        {
            UnitMutationResult.Success success => Results.Ok(UnitResponse.From(success.Unit)),
            _ => NotFound(),
        };
    }

    private static async Task<IResult> DeleteUnitAsync(
        long id,
        HttpContext context,
        ICurrentActorProvider actorProvider,
        UnitService units,
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

        var result = await units.DeleteAsync(id, ipAddress, actorUserId, cancellationToken);
        if (result is UnitMutationResult.Success)
        {
            await liveSync.SettingsUpdatedAsync(
                LiveSyncPublisherExtensions.UnitsEntity,
                actorUserId,
                id,
                cancellationToken);
        }

        return result switch
        {
            UnitMutationResult.Success => Results.NoContent(),
            UnitMutationResult.InUse inUse => UnitInUse(inUse.UsageCount),
            _ => NotFound(),
        };
    }

    /// <summary>
    /// Reads an optional <c>base_factor</c> property. Returns true when the
    /// property is present (regardless of validity) and records a field error
    /// for anything that is not a decimal number.
    /// </summary>
    private static bool TryReadFactor(
        JsonElement root,
        Dictionary<string, string[]> errors,
        out decimal? baseFactor)
    {
        baseFactor = null;
        if (!root.TryGetProperty("base_factor", out var factorElement))
        {
            return false;
        }

        if (factorElement.ValueKind != JsonValueKind.Number)
        {
            errors["base_factor"] = ["base_factor must be a number."];
        }
        else if (!factorElement.TryGetDecimal(out var parsed))
        {
            errors["base_factor"] = ["base_factor must be a valid decimal number."];
        }
        else
        {
            baseFactor = parsed;
        }

        return true;
    }

    private static async Task<(long? ActorUserId, string? IpAddress, IResult? Error)> ResolveActorAsync(
        HttpContext context,
        ICurrentActorProvider actorProvider,
        CancellationToken cancellationToken)
    {
        var actor = await actorProvider.GetCurrentActorAsync(cancellationToken);
        if (actor is null)
        {
            // The permission filter already rejected unauthenticated requests
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

    private static IResult UnitConflict(long? existingId)
    {
        var details = new Dictionary<string, object?>();
        if (existingId is not null)
        {
            details["existing_id"] = existingId.Value;
        }

        return ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.UnitExists,
            "A unit with this symbol or display name already exists.",
            details);
    }

    private static IResult AliasConflict(IReadOnlyDictionary<string, object?> details) =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.AliasExists,
            "The alias is already used on this unit.",
            details);

    private static IResult UnitInUse(int usageCount) =>
        ApiResults.Error(
            StatusCodes.Status409Conflict,
            ErrorCodes.UnitInUse,
            $"The unit is still referenced by {usageCount} recipe ingredient(s); retire it instead.",
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
            "The unit does not exist.");
}
