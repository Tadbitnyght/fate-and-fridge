using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>One recipe as exposed by the API and by audit snapshots (docs/DATABASE.md §3).</summary>
public sealed record RecipeRecord(
    long Id,
    string Name,
    string? Description,
    string? SourceSite,
    string? SourceUrl,
    string? Author,
    int? PrepMinutes,
    int? CookMinutes,
    string? Notes,
    RecipeStatus Status,
    bool IsFavorite,
    bool IsVegan,
    bool IsVegetarian,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A PATCH-style field: <see cref="IsSet"/> distinguishes an omitted property
/// from an explicitly supplied value, including the explicit null that clears an
/// optional field.
/// </summary>
public readonly record struct RecipeField<T>(bool IsSet, T Value)
{
    /// <summary>Marks <paramref name="value"/> as explicitly supplied.</summary>
    public static RecipeField<T> Set(T value) => new(true, value);
}

/// <summary>
/// Base recipe metadata accepted by create and update (M3-02, docs/M3_PLAN.md
/// §1.2–§1.4, §1.17–§1.19). Unset fields are unchanged on update and take
/// their defaults on create.
/// </summary>
public sealed record RecipeWriteRequest(
    RecipeField<string?> Name,
    RecipeField<string?> Description,
    RecipeField<string?> SourceSite,
    RecipeField<string?> SourceUrl,
    RecipeField<string?> Author,
    RecipeField<int?> PrepMinutes,
    RecipeField<int?> CookMinutes,
    RecipeField<string?> Notes,
    RecipeField<string?> Status,
    RecipeField<bool?> IsFavorite,
    RecipeField<bool?> IsVegan,
    RecipeField<bool?> IsVegetarian);

/// <summary>Outcome of a recipe create/update attempt.</summary>
public abstract record RecipeMutationResult
{
    private RecipeMutationResult()
    {
    }

    public sealed record Success(RecipeRecord Recipe) : RecipeMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : RecipeMutationResult;

    public sealed record NotFound : RecipeMutationResult;

    /// <summary>The normalized name is already taken; <c>details.existing_id</c> identifies the owner.</summary>
    public sealed record Conflict(long? ExistingId) : RecipeMutationResult;
}

/// <summary>
/// M3-02 recipe base metadata and status lifecycle (docs/M3_PLAN.md §1.2–§1.4,
/// §1.17–§1.19): display-name normalization with normalized-name uniqueness,
/// bounded optional text, http/https-only source URLs that are stored but never
/// fetched, nullable whole minutes, and an audit row staged into the caller's
/// <see cref="AppDbContext"/> for every write.
/// </summary>
public sealed class RecipeService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<RecipeService> logger)
{
    public const int MaxNameLength = 200;

    public const int MaxLongTextLength = 20_000;

    public const int MaxSourceSiteLength = 200;

    public const int MaxAuthorLength = 200;

    public const int MaxSourceUrlLength = 2048;

    public const int MinMinutes = 0;

    public const int MaxMinutes = 1440;

    private const string NameRequiredMessage = "name is required and must not be blank.";

    private const string NameNullMessage = "name cannot be null.";

    private const string NameControlMessage = "name contains unsupported control characters.";

    private const string NameTooLongMessage = "name must be at most 200 characters.";

    private const string DuplicateNameMessage = "A recipe with this name already exists.";

    /// <summary>
    /// Lists recipes ordered by <c>created_at DESC, id DESC</c> (the fixed
    /// <c>recently_added</c> sort until M3-09) together with the unpaged total.
    /// </summary>
    public async Task<(IReadOnlyList<RecipeRecord> Items, int Total)> ListAsync(
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Recipes.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var recipes = await query
            .OrderByDescending(recipe => recipe.CreatedAt)
            .ThenByDescending(recipe => recipe.Id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return (recipes.Select(ToRecord).ToArray(), total);
    }

    /// <summary>Reads one recipe's base metadata, or null when it does not exist.</summary>
    public async Task<RecipeRecord?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var recipe = await dbContext.Recipes.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return recipe is null ? null : ToRecord(recipe);
    }

    /// <summary>Creates a recipe and stages its <c>created</c> audit row atomically.</summary>
    public async Task<RecipeMutationResult> CreateAsync(
        RecipeWriteRequest request,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = NewErrors();
        var name = ReadName(request.Name, required: true, errors);
        var description = NormalizeText(request.Description, "description", MaxLongTextLength, allowLineWhitespace: true, errors);
        var notes = NormalizeText(request.Notes, "notes", MaxLongTextLength, allowLineWhitespace: true, errors);
        var sourceSite = NormalizeText(request.SourceSite, "source_site", MaxSourceSiteLength, allowLineWhitespace: false, errors);
        var author = NormalizeText(request.Author, "author", MaxAuthorLength, allowLineWhitespace: false, errors);
        var sourceUrl = NormalizeSourceUrl(request.SourceUrl, out var normalizedSourceUrl, errors);
        var prepMinutes = ReadMinutes(request.PrepMinutes, "prep_minutes", errors);
        var cookMinutes = ReadMinutes(request.CookMinutes, "cook_minutes", errors);
        var status = ReadStatus(request.Status, errors);
        if (errors.Count > 0)
        {
            return new RecipeMutationResult.ValidationFailed(errors);
        }

        if (await FindExistingIdAsync(name!.Normalized, excludingId: null, cancellationToken) is { } existingId)
        {
            return new RecipeMutationResult.Conflict(existingId);
        }

        var now = timeProvider.GetUtcNow();
        var recipe = new Recipe
        {
            Name = name.Display,
            NormalizedName = name.Normalized,
            Description = description,
            SourceSite = sourceSite,
            SourceUrl = sourceUrl,
            NormalizedSourceUrl = normalizedSourceUrl,
            Author = author,
            PrepMinutes = prepMinutes,
            CookMinutes = cookMinutes,
            Notes = notes,
            Status = status ?? RecipeStatus.Normal,
            IsFavorite = request.IsFavorite.Value == true,
            IsVegan = request.IsVegan.Value == true,
            IsVegetarian = request.IsVegetarian.Value == true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Recipes.Add(recipe);

        // Two saves (the generated id feeds the audit entity id), so they share
        // one explicit transaction: either both rows commit or neither does.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost a create race on normalized_name.
            dbContext.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return new RecipeMutationResult.Conflict(
                await FindExistingIdAsync(name.Normalized, excludingId: null, cancellationToken));
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Created,
                AuditEntityTypes.Recipe,
                EntityId: recipe.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(recipe),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created recipe {RecipeId}", recipe.Id);
        return new RecipeMutationResult.Success(ToRecord(recipe));
    }

    /// <summary>
    /// Applies a partial base-metadata update: omitted fields are unchanged and
    /// an explicit null clears an optional field. The name cannot be null.
    /// </summary>
    public async Task<RecipeMutationResult> UpdateAsync(
        long id,
        RecipeWriteRequest request,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var recipe = await dbContext.Recipes.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (recipe is null)
        {
            return new RecipeMutationResult.NotFound();
        }

        var errors = NewErrors();
        var name = ReadName(request.Name, required: false, errors);
        var description = NormalizeText(request.Description, "description", MaxLongTextLength, allowLineWhitespace: true, errors);
        var notes = NormalizeText(request.Notes, "notes", MaxLongTextLength, allowLineWhitespace: true, errors);
        var sourceSite = NormalizeText(request.SourceSite, "source_site", MaxSourceSiteLength, allowLineWhitespace: false, errors);
        var author = NormalizeText(request.Author, "author", MaxAuthorLength, allowLineWhitespace: false, errors);
        var sourceUrl = NormalizeSourceUrl(request.SourceUrl, out var normalizedSourceUrl, errors);
        var prepMinutes = ReadMinutes(request.PrepMinutes, "prep_minutes", errors);
        var cookMinutes = ReadMinutes(request.CookMinutes, "cook_minutes", errors);
        var status = ReadStatus(request.Status, errors);
        if (errors.Count > 0)
        {
            return new RecipeMutationResult.ValidationFailed(errors);
        }

        if (name is not null
            && !string.Equals(name.Normalized, recipe.NormalizedName, StringComparison.Ordinal)
            && await FindExistingIdAsync(name.Normalized, recipe.Id, cancellationToken) is { } existingId)
        {
            return new RecipeMutationResult.Conflict(existingId);
        }

        var before = SnapshotJson(recipe);
        if (name is not null)
        {
            recipe.Name = name.Display;
            recipe.NormalizedName = name.Normalized;
        }

        if (request.Description.IsSet)
        {
            recipe.Description = description;
        }

        if (request.SourceSite.IsSet)
        {
            recipe.SourceSite = sourceSite;
        }

        if (request.SourceUrl.IsSet)
        {
            recipe.SourceUrl = sourceUrl;
            recipe.NormalizedSourceUrl = normalizedSourceUrl;
        }

        if (request.Author.IsSet)
        {
            recipe.Author = author;
        }

        if (request.PrepMinutes.IsSet)
        {
            recipe.PrepMinutes = prepMinutes;
        }

        if (request.CookMinutes.IsSet)
        {
            recipe.CookMinutes = cookMinutes;
        }

        if (request.Notes.IsSet)
        {
            recipe.Notes = notes;
        }

        if (status is not null)
        {
            recipe.Status = status.Value;
        }

        if (request.IsFavorite.Value is { } isFavorite)
        {
            recipe.IsFavorite = isFavorite;
        }

        if (request.IsVegan.Value is { } isVegan)
        {
            recipe.IsVegan = isVegan;
        }

        if (request.IsVegetarian.Value is { } isVegetarian)
        {
            recipe.IsVegetarian = isVegetarian;
        }

        recipe.UpdatedAt = timeProvider.GetUtcNow();

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Recipe,
                EntityId: recipe.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(recipe),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost a rename race on normalized_name.
            dbContext.ChangeTracker.Clear();
            return new RecipeMutationResult.Conflict(
                await FindExistingIdAsync(name?.Normalized, recipe.Id, cancellationToken));
        }

        logger.LogInformation("Updated recipe {RecipeId}", id);
        return new RecipeMutationResult.Success(ToRecord(recipe));
    }

    private static Dictionary<string, string[]> NewErrors() => new(StringComparer.Ordinal);

    private static ReferenceDataNormalizationResult.Success? ReadName(
        RecipeField<string?> field,
        bool required,
        Dictionary<string, string[]> errors)
    {
        if (!field.IsSet)
        {
            if (required)
            {
                errors["name"] = [NameRequiredMessage];
            }

            return null;
        }

        if (field.Value is null)
        {
            errors["name"] = [required ? NameRequiredMessage : NameNullMessage];
            return null;
        }

        var normalization = ReferenceDataNormalizer.Normalize(field.Value);
        if (normalization is not ReferenceDataNormalizationResult.Success normalized)
        {
            errors["name"] =
            [
                normalization is ReferenceDataNormalizationResult.Invalid
                {
                    Reason: ReferenceDataNormalizationFailureReason.ControlCharacters,
                }
                    ? NameControlMessage
                    : NameRequiredMessage,
            ];
            return null;
        }

        if (normalized.Display.Length > MaxNameLength)
        {
            errors["name"] = [NameTooLongMessage];
            return null;
        }

        return normalized;
    }

    private static string? NormalizeText(
        RecipeField<string?> field,
        string fieldName,
        int maxLength,
        bool allowLineWhitespace,
        Dictionary<string, string[]> errors)
    {
        if (!field.IsSet || field.Value is null)
        {
            return null;
        }

        var trimmed = field.Value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (ContainsDisallowedControlCharacter(trimmed, allowLineWhitespace))
        {
            errors[fieldName] = [$"{fieldName} contains unsupported control characters."];
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            errors[fieldName] = [$"{fieldName} must be at most {maxLength} characters."];
            return null;
        }

        return trimmed;
    }

    private static string? NormalizeSourceUrl(
        RecipeField<string?> field,
        out string? normalizedSourceUrl,
        Dictionary<string, string[]> errors)
    {
        normalizedSourceUrl = null;
        if (!field.IsSet || field.Value is null)
        {
            return null;
        }

        var trimmed = field.Value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > MaxSourceUrlLength)
        {
            errors["source_url"] = [$"source_url must be at most {MaxSourceUrlLength} characters."];
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            errors["source_url"] = ["source_url must be an absolute http or https URL."];
            return null;
        }

        normalizedSourceUrl = BuildNormalizedSourceUrl(uri, trimmed);
        return trimmed;
    }

    /// <summary>
    /// Minimal source-URL normalization (docs/M3_PLAN.md §1.19): lowercase the
    /// scheme and host, drop a default port, and drop the fragment. Nothing else
    /// is rewritten, and M3 never fetches the URL.
    /// </summary>
    private static string BuildNormalizedSourceUrl(Uri uri, string raw)
    {
        var normalized = raw;

        var fragmentIndex = normalized.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            normalized = normalized[..fragmentIndex];
        }

        var schemeSeparator = normalized.IndexOf(':');
        if (schemeSeparator > 0)
        {
            normalized = string.Concat(
                normalized[..schemeSeparator].ToLowerInvariant(),
                normalized[schemeSeparator..]);
        }

        var authorityStart = normalized.IndexOf("://", StringComparison.Ordinal) + 3;
        if (authorityStart >= 3 && authorityStart <= normalized.Length)
        {
            var slashIndex = normalized.IndexOf('/', authorityStart);
            var queryIndex = normalized.IndexOf('?', authorityStart);
            var authorityEnd = Math.Min(
                slashIndex < 0 ? normalized.Length : slashIndex,
                queryIndex < 0 ? normalized.Length : queryIndex);
            var authority = normalized[authorityStart..authorityEnd];

            var hostStartInAuthority = authority.IndexOf('@') + 1;
            var hostPort = authority[hostStartInAuthority..];
            var portStart = hostPort.StartsWith('[')
                ? hostPort.IndexOf(']') + 1
                : hostPort.IndexOf(':');
            var host = portStart < 0 ? hostPort : hostPort[..portStart];
            var port = portStart < 0 ? string.Empty : hostPort[portStart..];
            if (uri.IsDefaultPort)
            {
                port = string.Empty;
            }

            var normalizedAuthority = string.Concat(
                authority[..hostStartInAuthority],
                host.ToLowerInvariant(),
                port);
            normalized = string.Concat(
                normalized[..authorityStart],
                normalizedAuthority,
                normalized[authorityEnd..]);
        }

        return normalized;
    }

    private static int? ReadMinutes(
        RecipeField<int?> field,
        string fieldName,
        Dictionary<string, string[]> errors)
    {
        if (!field.IsSet || field.Value is null)
        {
            return null;
        }

        if (field.Value.Value < MinMinutes || field.Value.Value > MaxMinutes)
        {
            errors[fieldName] = [$"{fieldName} must be between {MinMinutes} and {MaxMinutes} minutes."];
            return null;
        }

        return field.Value.Value;
    }

    private static RecipeStatus? ReadStatus(
        RecipeField<string?> field,
        Dictionary<string, string[]> errors)
    {
        if (!field.IsSet)
        {
            return null;
        }

        if (EnumTextConverters.TryRecipeStatusFromText(field.Value, out var status))
        {
            return status;
        }

        errors["status"] = ["status must be one of normal, manual_only, or retired."];
        return null;
    }

    private static bool ContainsDisallowedControlCharacter(string value, bool allowLineWhitespace)
    {
        foreach (var character in value)
        {
            if (allowLineWhitespace && character is '\n' or '\r' or '\t')
            {
                continue;
            }

            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<long?> FindExistingIdAsync(
        string? normalizedName,
        long? excludingId,
        CancellationToken cancellationToken)
    {
        if (normalizedName is null)
        {
            return null;
        }

        var query = dbContext.Recipes.AsNoTracking();
        if (excludingId is not null)
        {
            query = query.Where(recipe => recipe.Id != excludingId.Value);
        }

        return await query
            .Where(recipe => recipe.NormalizedName == normalizedName)
            .OrderBy(recipe => recipe.Id)
            .Select(recipe => (long?)recipe.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string SnapshotJson(Recipe recipe) =>
        JsonSerializer.Serialize(new
        {
            id = recipe.Id,
            name = recipe.Name,
            normalized_name = recipe.NormalizedName,
            description = recipe.Description,
            source_site = recipe.SourceSite,
            source_url = recipe.SourceUrl,
            normalized_source_url = recipe.NormalizedSourceUrl,
            author = recipe.Author,
            prep_minutes = recipe.PrepMinutes,
            cook_minutes = recipe.CookMinutes,
            notes = recipe.Notes,
            status = EnumTextConverters.RecipeStatusToText(recipe.Status),
            is_favorite = recipe.IsFavorite,
            is_vegan = recipe.IsVegan,
            is_vegetarian = recipe.IsVegetarian,
        });

    private static RecipeRecord ToRecord(Recipe recipe) =>
        new(
            recipe.Id,
            recipe.Name,
            recipe.Description,
            recipe.SourceSite,
            recipe.SourceUrl,
            recipe.Author,
            recipe.PrepMinutes,
            recipe.CookMinutes,
            recipe.Notes,
            recipe.Status,
            recipe.IsFavorite,
            recipe.IsVegan,
            recipe.IsVegetarian,
            recipe.CreatedAt,
            recipe.UpdatedAt);
}
