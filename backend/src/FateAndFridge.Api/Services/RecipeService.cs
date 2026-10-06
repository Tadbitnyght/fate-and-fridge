using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>Ingredient reference embedded in a recipe ingredient line.</summary>
public sealed record RecipeIngredientReference(
    long Id,
    string Name,
    bool IsActive);

/// <summary>A nullable unit reference embedded in a recipe ingredient line.</summary>
public sealed record RecipeUnitReference(
    long Id,
    string Symbol,
    string DisplayName,
    bool IsActive);

/// <summary>
/// One ingredient line of a recipe (M3-06, docs/M3_PLAN.md §1.8–§1.9). The
/// quantity is a precise decimal or null ("to taste"); the unit is optional.
/// </summary>
public sealed record RecipeIngredientRecord(
    long Id,
    RecipeIngredientReference Ingredient,
    decimal? Quantity,
    RecipeUnitReference? Unit,
    string? PreparationNote,
    bool IsOptional,
    int SortOrder);

/// <summary>An optional ordered ingredient group with its nested ingredient lines.</summary>
public sealed record RecipeIngredientGroupRecord(
    long Id,
    string? Title,
    int SortOrder,
    IReadOnlyList<RecipeIngredientRecord> Ingredients);

/// <summary>One ordered instruction step (M3-07).</summary>
public sealed record RecipeInstructionStepRecord(
    long Id,
    string Text,
    int SortOrder);

/// <summary>An optional ordered instruction section with its nested steps.</summary>
public sealed record RecipeInstructionSectionRecord(
    long Id,
    string? Title,
    int SortOrder,
    IReadOnlyList<RecipeInstructionStepRecord> Steps);

/// <summary>A protein association as displayed on a recipe.</summary>
public sealed record RecipeProteinRecord(
    long Id,
    string Name,
    bool IsActive);

/// <summary>A tag association as displayed on a recipe.</summary>
public sealed record RecipeTagRecord(
    long Id,
    string Name);

/// <summary>
/// One recipe as exposed by the API and by audit snapshots (docs/DATABASE.md
/// §3, §6, §7), including its active image (M3-04) and its nested ingredient,
/// instruction, protein, and tag slices (M3-05 … M3-08).
/// </summary>
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
    DateTimeOffset UpdatedAt,
    RecipeImageRecord? Image,
    IReadOnlyList<RecipeIngredientGroupRecord> IngredientGroups,
    IReadOnlyList<RecipeIngredientRecord> Ingredients,
    IReadOnlyList<RecipeInstructionSectionRecord> InstructionSections,
    IReadOnlyList<RecipeProteinRecord> Proteins,
    IReadOnlyList<RecipeTagRecord> Tags);

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

/// <summary>One ingredient line supplied by create/update (M3-06).</summary>
public sealed record RecipeIngredientWrite(
    long IngredientId,
    decimal? Quantity,
    long? UnitId,
    string? PreparationNote,
    bool IsOptional);

/// <summary>One ordered ingredient group supplied by create/update (M3-05/M3-06).</summary>
public sealed record RecipeIngredientGroupWrite(
    string? Title,
    IReadOnlyList<RecipeIngredientWrite> Ingredients);

/// <summary>One ordered instruction section supplied by create/update (M3-07).</summary>
public sealed record RecipeInstructionSectionWrite(
    string? Title,
    IReadOnlyList<string> Steps);

/// <summary>
/// Recipe metadata and nested collections accepted by create and update
/// (M3-02 … M3-09, docs/M3_PLAN.md §1). Unset fields are unchanged on update
/// and take their defaults on create. A set collection is a full replacement of
/// that slice.
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
    RecipeField<bool?> IsVegetarian,
    RecipeField<IReadOnlyList<RecipeIngredientGroupWrite>> IngredientGroups,
    RecipeField<IReadOnlyList<RecipeIngredientWrite>> Ingredients,
    RecipeField<IReadOnlyList<RecipeInstructionSectionWrite>> InstructionSections,
    RecipeField<IReadOnlyList<long>> ProteinIds,
    RecipeField<IReadOnlyList<long>> TagIds);

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
/// M3 recipe service (docs/M3_PLAN.md §1–§2): base metadata and status
/// lifecycle (M3-02), favorite/diet flags (M3-03), image attachment (M3-04),
/// ingredient groups and ingredients (M3-05/M3-06), instruction sections and
/// steps (M3-07), protein/tag associations (M3-08), and the full authorized
/// document CRUD contract (M3-09). Every write stages one audit row into the
/// caller's <see cref="AppDbContext"/> and commits as a single transaction.
/// </summary>
public sealed class RecipeService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    HouseholdClock clock,
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

    public const decimal MaxQuantity = 1_000_000m;

    public const int MaxQuantityScale = 14;

    /// <summary>Unique index that guards the recipe display-name lookup form.</summary>
    private const string RecipeNameConstraint = "ix_recipes_normalized_name";

    /// <summary>Named sorts accepted by <c>GET /recipes</c> (M3-09).</summary>
    public static readonly IReadOnlyList<string> SortValues =
        ["recently_added", "name", "favorites", "recently_eaten", "least_recently_eaten", "most_eaten"];

    private const string NameRequiredMessage = "name is required and must not be blank.";

    private const string NameNullMessage = "name cannot be null.";

    private const string NameControlMessage = "name contains unsupported control characters.";

    private const string NameTooLongMessage = "name must be at most 200 characters.";

    /// <summary>
    /// Lists recipes matching the filters and named sort, together with the
    /// unpaged total (M3-09, docs/M3_PLAN.md §1.14–§1.16). History-dependent
    /// sorts are deterministic until M11 provides history data: they fall back
    /// to the <c>recently_added</c> order.
    /// </summary>
    public async Task<(IReadOnlyList<RecipeRecord> Items, int Total)> ListAsync(
        int limit,
        int offset,
        string? normalizedSearch = null,
        RecipeStatus? status = null,
        bool? isFavorite = null,
        bool? isVegan = null,
        bool? isVegetarian = null,
        long? proteinId = null,
        long? tagId = null,
        string sort = "recently_added",
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Recipes.AsNoTracking();

        if (!string.IsNullOrEmpty(normalizedSearch))
        {
            query = query.Where(recipe => recipe.NormalizedName.Contains(normalizedSearch));
        }

        if (status is not null)
        {
            query = query.Where(recipe => recipe.Status == status.Value);
        }

        if (isFavorite is not null)
        {
            query = query.Where(recipe => recipe.IsFavorite == isFavorite.Value);
        }

        if (isVegan is not null)
        {
            query = query.Where(recipe => recipe.IsVegan == isVegan.Value);
        }

        if (isVegetarian is not null)
        {
            query = query.Where(recipe => recipe.IsVegetarian == isVegetarian.Value);
        }

        if (proteinId is not null)
        {
            var protein = proteinId.Value;
            query = query.Where(recipe =>
                dbContext.RecipeProteins.Any(link => link.RecipeId == recipe.Id && link.ProteinId == protein));
        }

        if (tagId is not null)
        {
            var tag = tagId.Value;
            query = query.Where(recipe =>
                dbContext.RecipeTags.Any(link => link.RecipeId == recipe.Id && link.TagId == tag));
        }

        var total = await query.CountAsync(cancellationToken);

        query = sort switch
        {
            "name" => query.OrderBy(recipe => recipe.Name).ThenBy(recipe => recipe.Id),
            "favorites" => query
                .OrderByDescending(recipe => recipe.IsFavorite)
                .ThenByDescending(recipe => recipe.CreatedAt)
                .ThenByDescending(recipe => recipe.Id),
            // recently_eaten / least_recently_eaten / most_eaten become real
            // once M5/M11 record history; until then they use the default order.
            _ => query.OrderByDescending(recipe => recipe.CreatedAt).ThenByDescending(recipe => recipe.Id),
        };

        var recipes = await query
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var images = await LoadImagesAsync(
            recipes.Select(recipe => recipe.Id).ToArray(),
            cancellationToken);

        return (
            recipes
                .Select(recipe => ToRecord(recipe, images.GetValueOrDefault(recipe.Id)))
                .ToArray(),
            total);
    }

    /// <summary>Reads one recipe's complete document, or null when it does not exist.</summary>
    public async Task<RecipeRecord?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var recipe = await dbContext.Recipes.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (recipe is null)
        {
            return null;
        }

        var nested = await LoadNestedAsync(id, cancellationToken);
        return ToRecord(recipe, await LoadImageAsync(id, cancellationToken), nested);
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
        var metadata = ReadMetadata(request, errors);
        if (errors.Count > 0)
        {
            return new RecipeMutationResult.ValidationFailed(errors);
        }

        if (await FindExistingIdAsync(name!.Normalized, excludingId: null, cancellationToken) is { } existingId)
        {
            return new RecipeMutationResult.Conflict(existingId);
        }

        // Resolve every nested slice before writing anything so a rejection
        // cannot leave a partial recipe or audit row behind.
        var nested = await ResolveNestedAsync(request, ExistingReferences.Empty, errors, cancellationToken);
        if (errors.Count > 0)
        {
            return new RecipeMutationResult.ValidationFailed(errors);
        }

        var now = timeProvider.GetUtcNow();
        var recipe = new Recipe
        {
            Name = name.Display,
            NormalizedName = name.Normalized,
            Description = metadata.Description,
            SourceSite = metadata.SourceSite,
            SourceUrl = metadata.SourceUrl,
            NormalizedSourceUrl = metadata.NormalizedSourceUrl,
            Author = metadata.Author,
            PrepMinutes = metadata.PrepMinutes,
            CookMinutes = metadata.CookMinutes,
            Notes = metadata.Notes,
            Status = metadata.Status ?? RecipeStatus.Normal,
            IsFavorite = request.IsFavorite.Value == true,
            IsVegan = request.IsVegan.Value == true,
            IsVegetarian = request.IsVegetarian.Value == true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Recipes.Add(recipe);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // The generated id feeds the nested rows and the audit entity id.
            await dbContext.SaveChangesAsync(cancellationToken);
            await InsertNestedAsync(recipe.Id, nested, cancellationToken);
            var document = await LoadNestedAsync(recipe.Id, cancellationToken);

            await auditService.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.Created,
                    AuditEntityTypes.Recipe,
                    EntityId: recipe.Id.ToString(CultureInfo.InvariantCulture),
                    AfterJson: SnapshotJson(recipe, document),
                    IpAddress: ipAddress,
                    ActorUserId: actorUserId),
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            PostgresErrors.IsUniqueViolationOn(exception, RecipeNameConstraint))
        {
            // Lost a create race on normalized_name.
            dbContext.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return new RecipeMutationResult.Conflict(
                await FindExistingIdAsync(name.Normalized, excludingId: null, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created recipe {RecipeId}", recipe.Id);
        return new RecipeMutationResult.Success(
            ToRecord(recipe, image: null, await LoadNestedAsync(recipe.Id, cancellationToken)));
    }

    /// <summary>
    /// Applies a partial update: omitted fields are unchanged and an explicit
    /// null clears an optional field. A set collection slice is a full
    /// replacement. The whole update, including nested rows and the audit row,
    /// commits in one transaction.
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
        var metadata = ReadMetadata(request, errors);
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

        var existing = await LoadExistingReferencesAsync(recipe.Id, cancellationToken);
        var nested = await ResolveNestedAsync(request, existing, errors, cancellationToken);
        if (errors.Count > 0)
        {
            return new RecipeMutationResult.ValidationFailed(errors);
        }

        var beforeJson = SnapshotJson(recipe, await LoadNestedAsync(recipe.Id, cancellationToken));
        var recipeId = recipe.Id;

        var renamed = name is not null
            && !string.Equals(name.Normalized, recipe.NormalizedName, StringComparison.Ordinal);
        if (name is not null)
        {
            recipe.Name = name.Display;
            recipe.NormalizedName = name.Normalized;
        }

        ApplyMetadataFields(recipe, request, metadata);
        recipe.UpdatedAt = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Full-replacement slices are deleted at the database level inside
            // the transaction so the new rows and the audit snapshot agree.
            if (request.IngredientGroups.IsSet)
            {
                await dbContext.RecipeIngredientGroups
                    .Where(group => group.RecipeId == recipeId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (request.Ingredients.IsSet)
            {
                await dbContext.RecipeIngredients
                    .Where(ingredient => ingredient.RecipeId == recipeId && ingredient.GroupId == null)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (request.InstructionSections.IsSet)
            {
                await dbContext.RecipeInstructionSections
                    .Where(section => section.RecipeId == recipeId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (request.ProteinIds.IsSet)
            {
                await dbContext.RecipeProteins
                    .Where(link => link.RecipeId == recipeId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            if (request.TagIds.IsSet)
            {
                await dbContext.RecipeTags
                    .Where(link => link.RecipeId == recipeId)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await InsertNestedAsync(recipeId, nested, cancellationToken);
            var document = await LoadNestedAsync(recipeId, cancellationToken);

            await auditService.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.Updated,
                    AuditEntityTypes.Recipe,
                    EntityId: recipeId.ToString(CultureInfo.InvariantCulture),
                    BeforeJson: beforeJson,
                    AfterJson: SnapshotJson(recipe, document),
                    IpAddress: ipAddress,
                    ActorUserId: actorUserId),
                cancellationToken);

            if (renamed)
            {
                // Refresh future/today calendar snapshots; historical entries
                // stay frozen (docs/M5_PLAN.md §1.5).
                await CalendarService.RefreshSnapshotsAsync(
                    dbContext,
                    clock,
                    recipeId,
                    recipe.Name,
                    cancellationToken).ConfigureAwait(false);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            PostgresErrors.IsUniqueViolationOn(exception, RecipeNameConstraint))
        {
            // Lost a rename race on normalized_name.
            dbContext.ChangeTracker.Clear();
            await transaction.RollbackAsync(cancellationToken);
            return new RecipeMutationResult.Conflict(
                await FindExistingIdAsync(name?.Normalized, recipeId, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Updated recipe {RecipeId}", recipeId);
        return new RecipeMutationResult.Success(
            ToRecord(recipe, await LoadImageAsync(recipeId, cancellationToken), await LoadNestedAsync(recipeId, cancellationToken)));
    }

    private static Dictionary<string, string[]> NewErrors() => new(StringComparer.Ordinal);

    private static MetadataValues ReadMetadata(
        RecipeWriteRequest request,
        Dictionary<string, string[]> errors)
    {
        var sourceUrl = NormalizeSourceUrl(request.SourceUrl, out var normalizedSourceUrl, errors);
        return new MetadataValues(
            NormalizeText(request.Description, "description", MaxLongTextLength, allowLineWhitespace: true, errors),
            NormalizeText(request.SourceSite, "source_site", MaxSourceSiteLength, allowLineWhitespace: false, errors),
            sourceUrl,
            normalizedSourceUrl,
            NormalizeText(request.Author, "author", MaxAuthorLength, allowLineWhitespace: false, errors),
            ReadMinutes(request.PrepMinutes, "prep_minutes", errors),
            ReadMinutes(request.CookMinutes, "cook_minutes", errors),
            NormalizeText(request.Notes, "notes", MaxLongTextLength, allowLineWhitespace: true, errors),
            ReadStatus(request.Status, errors));
    }

    private static void ApplyMetadataFields(Recipe recipe, RecipeWriteRequest request, MetadataValues metadata)
    {
        if (request.Description.IsSet)
        {
            recipe.Description = metadata.Description;
        }

        if (request.SourceSite.IsSet)
        {
            recipe.SourceSite = metadata.SourceSite;
        }

        if (request.SourceUrl.IsSet)
        {
            recipe.SourceUrl = metadata.SourceUrl;
            recipe.NormalizedSourceUrl = metadata.NormalizedSourceUrl;
        }

        if (request.Author.IsSet)
        {
            recipe.Author = metadata.Author;
        }

        if (request.PrepMinutes.IsSet)
        {
            recipe.PrepMinutes = metadata.PrepMinutes;
        }

        if (request.CookMinutes.IsSet)
        {
            recipe.CookMinutes = metadata.CookMinutes;
        }

        if (request.Notes.IsSet)
        {
            recipe.Notes = metadata.Notes;
        }

        if (metadata.Status is not null)
        {
            recipe.Status = metadata.Status.Value;
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
    }

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
        Dictionary<string, string[]> errors) =>
        NormalizeText(field.IsSet ? field.Value : null, fieldName, maxLength, allowLineWhitespace, errors);

    private static string? NormalizeText(
        string? value,
        string fieldName,
        int maxLength,
        bool allowLineWhitespace,
        Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
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

        if (ContainsDisallowedControlCharacter(trimmed, allowLineWhitespace: false))
        {
            errors["source_url"] = ["source_url must not contain control characters."];
            return null;
        }

        normalizedSourceUrl = BuildNormalizedSourceUrl(uri, trimmed);
        return trimmed;
    }

    /// <summary>
    /// Canonical source-URL normalization shared by recipe save and M4 import
    /// duplicate detection (docs/M3_PLAN.md §1.19, docs/M4_PLAN.md §1.9):
    /// lowercase the scheme and host, drop a default port, drop the fragment,
    /// trim a trailing slash from a non-root path, and sort query parameters.
    /// Nothing else is rewritten and the URL is never fetched. This is the form
    /// stored in <see cref="Recipe.NormalizedSourceUrl"/>; callers must not fork
    /// the normalization so import detection and recipe save agree.
    /// </summary>
    /// <remarks>
    /// Canonicalization here is intentionally narrow and best-effort: it folds
    /// the variants the M4 duplicate warning must tolerate (trailing slash,
    /// query-parameter order, fragment, case, default port). It does not resolve
    /// redirects, percent-decode, or otherwise reinterpret the path.
    /// </remarks>
    public static string BuildNormalizedSourceUrl(Uri uri, string raw)
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

        // Split path and query after authority normalization so the separate
        // path/query canonicalization below cannot disturb the host handling.
        var queryStart = normalized.IndexOf('?');
        var path = queryStart < 0 ? normalized : normalized[..queryStart];
        var query = queryStart < 0 ? null : normalized[queryStart..];

        // A trailing slash is not meaningful for equality here: "/recipe/" and
        // "/recipe" are the same page, and "https://example.com/" collapses to
        // "https://example.com" so a bare origin and its root-path form agree.
        var trimmedPath = path.TrimEnd('/');

        if (query is null)
        {
            return trimmedPath;
        }

        // Query-parameter order is not significant for duplicate detection, so
        // sort the pairs deterministically (stable by encoded pair). Blank
        // parameters are preserved so two distinct query strings never collapse
        // onto one another.
        var sortedQuery = SortQueryParameters(query);
        return string.Concat(trimmedPath, sortedQuery);
    }

    /// <summary>
    /// Sorts the query portion (including the leading <c>?</c>) by ordinal pair
    /// order so parameter order does not affect equality. The original encoding
    /// is preserved; parameters are never decoded or rewritten.
    /// </summary>
    private static string SortQueryParameters(string query)
    {
        var body = query[1..];
        if (body.Length == 0)
        {
            return "?";
        }

        var parts = body.Split('&');
        Array.Sort(parts, StringComparer.Ordinal);
        return string.Concat("?", string.Join('&', parts));
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

    /// <summary>References already attached to the recipe, used to permit retained retired data.</summary>
    private sealed record ExistingReferences(
        HashSet<long> IngredientIds,
        HashSet<long> UnitIds,
        HashSet<long> ProteinIds)
    {
        public static ExistingReferences Empty => new([], [], []);
    }

    private async Task<ExistingReferences> LoadExistingReferencesAsync(
        long recipeId,
        CancellationToken cancellationToken)
    {
        var ingredients = await dbContext.RecipeIngredients.AsNoTracking()
            .Where(ingredient => ingredient.RecipeId == recipeId)
            .Select(ingredient => new { ingredient.IngredientId, ingredient.UnitId })
            .ToListAsync(cancellationToken);
        var ingredientIds = ingredients.Select(ingredient => ingredient.IngredientId).ToHashSet();
        var unitIds = ingredients
            .Where(ingredient => ingredient.UnitId is not null)
            .Select(ingredient => ingredient.UnitId!.Value)
            .ToHashSet();
        var proteinIds = (await dbContext.RecipeProteins.AsNoTracking()
                .Where(link => link.RecipeId == recipeId)
                .Select(link => link.ProteinId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        return new ExistingReferences(ingredientIds, unitIds, proteinIds);
    }

    /// <summary>Resolved nested slices ready to be inserted.</summary>
    private sealed record ResolvedNested(
        List<ResolvedGroup> Groups,
        List<ResolvedIngredient> Ungrouped,
        List<ResolvedSection> Sections,
        List<long> ProteinIds,
        List<long> TagIds);

    private sealed record ResolvedGroup(
        string? Title,
        List<ResolvedIngredient> Ingredients);

    private sealed record ResolvedSection(
        string? Title,
        List<string> Steps);

    private sealed record ResolvedIngredient(
        RecipeIngredientWrite Write,
        Ingredient Ingredient,
        Unit? Unit);

    private async Task<ResolvedNested> ResolveNestedAsync(
        RecipeWriteRequest request,
        ExistingReferences existing,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        var groups = new List<ResolvedGroup>();
        var ungrouped = new List<ResolvedIngredient>();
        var sections = new List<ResolvedSection>();

        if (request.IngredientGroups.IsSet)
        {
            for (var groupIndex = 0; groupIndex < request.IngredientGroups.Value.Count; groupIndex++)
            {
                var group = request.IngredientGroups.Value[groupIndex];
                var title = NormalizeText(
                    group.Title,
                    $"ingredient_groups[{groupIndex}].title",
                    MaxLongTextLength,
                    allowLineWhitespace: true,
                    errors);
                var items = await ResolveIngredientsAsync(
                    group.Ingredients,
                    $"ingredient_groups[{groupIndex}].ingredients",
                    existing,
                    errors,
                    cancellationToken);
                groups.Add(new ResolvedGroup(title, items));
            }
        }

        if (request.Ingredients.IsSet)
        {
            ungrouped = await ResolveIngredientsAsync(
                request.Ingredients.Value,
                "ingredients",
                existing,
                errors,
                cancellationToken);
        }

        if (request.InstructionSections.IsSet)
        {
            for (var sectionIndex = 0; sectionIndex < request.InstructionSections.Value.Count; sectionIndex++)
            {
                var section = request.InstructionSections.Value[sectionIndex];
                var title = NormalizeText(
                    section.Title,
                    $"instruction_sections[{sectionIndex}].title",
                    MaxLongTextLength,
                    allowLineWhitespace: true,
                    errors);
                var steps = new List<string>();
                for (var stepIndex = 0; stepIndex < section.Steps.Count; stepIndex++)
                {
                    var key = $"instruction_sections[{sectionIndex}].steps[{stepIndex}]";
                    var text = section.Steps[stepIndex];
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        errors[key] = ["Step text is required and must not be blank."];
                        continue;
                    }

                    var normalized = NormalizeText(text, key, MaxLongTextLength, allowLineWhitespace: true, errors);
                    if (normalized is not null)
                    {
                        steps.Add(normalized);
                    }
                }

                sections.Add(new ResolvedSection(title, steps));
            }
        }

        var proteinIds = request.ProteinIds.IsSet
            ? await ResolveProteinsAsync(request.ProteinIds.Value, existing, errors, cancellationToken)
            : [];
        var tagIds = request.TagIds.IsSet
            ? await ResolveTagsAsync(request.TagIds.Value, errors, cancellationToken)
            : [];

        return new ResolvedNested(groups, ungrouped, sections, proteinIds, tagIds);
    }

    private async Task<List<ResolvedIngredient>> ResolveIngredientsAsync(
        IReadOnlyList<RecipeIngredientWrite> items,
        string keyPrefix,
        ExistingReferences existing,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ResolvedIngredient>(items.Count);
        if (items.Count == 0)
        {
            return resolved;
        }

        var ingredientIds = items.Select(item => item.IngredientId).Distinct().ToArray();
        var ingredientById = await dbContext.Ingredients.AsNoTracking()
            .Where(ingredient => ingredientIds.Contains(ingredient.Id))
            .ToDictionaryAsync(ingredient => ingredient.Id, cancellationToken);

        var unitIds = items
            .Where(item => item.UnitId is not null)
            .Select(item => item.UnitId!.Value)
            .Distinct()
            .ToArray();
        var unitById = unitIds.Length == 0
            ? []
            : await dbContext.Units.AsNoTracking()
                .Where(unit => unitIds.Contains(unit.Id))
                .ToDictionaryAsync(unit => unit.Id, cancellationToken);

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var key = $"{keyPrefix}[{index}]";
            var valid = true;

            if (!ingredientById.TryGetValue(item.IngredientId, out var ingredient))
            {
                errors[$"{key}.ingredient_id"] = ["ingredient_id must reference an existing ingredient."];
                valid = false;
                ingredient = null!;
            }
            else if (!ingredient.IsActive && !existing.IngredientIds.Contains(ingredient.Id))
            {
                errors[$"{key}.ingredient_id"] =
                    ["A retired ingredient cannot be newly selected; only existing references may be kept."];
                valid = false;
            }

            if (item.Quantity is { } quantity
                && (quantity < 0m || quantity > MaxQuantity || quantity != decimal.Round(quantity, MaxQuantityScale)))
            {
                errors[$"{key}.quantity"] =
                    ["quantity must be between 0 and 1,000,000 with at most 14 decimal places."];
                valid = false;
            }

            Unit? unit = null;
            if (item.UnitId is { } unitId)
            {
                if (!unitById.TryGetValue(unitId, out unit))
                {
                    errors[$"{key}.unit_id"] = ["unit_id must reference an existing unit."];
                    valid = false;
                }
                else if (!unit.IsActive && !existing.UnitIds.Contains(unit.Id))
                {
                    errors[$"{key}.unit_id"] =
                        ["A retired unit cannot be newly selected; only existing references may be kept."];
                    valid = false;
                }
            }

            var preparationNote = NormalizeText(
                item.PreparationNote,
                $"{key}.preparation_note",
                MaxLongTextLength,
                allowLineWhitespace: true,
                errors);

            if (valid && ingredient is not null)
            {
                resolved.Add(new ResolvedIngredient(
                    item with { PreparationNote = preparationNote },
                    ingredient,
                    unit));
            }
        }

        return resolved;
    }

    private async Task<List<long>> ResolveProteinsAsync(
        IReadOnlyList<long> proteinIds,
        ExistingReferences existing,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (proteinIds.Count == 0)
        {
            return [];
        }

        var distinct = proteinIds.Distinct().ToArray();
        var byId = await dbContext.Proteins.AsNoTracking()
            .Where(protein => distinct.Contains(protein.Id))
            .ToDictionaryAsync(protein => protein.Id, cancellationToken);

        var resolved = new List<long>();
        var seen = new HashSet<long>();
        for (var index = 0; index < proteinIds.Count; index++)
        {
            var proteinId = proteinIds[index];
            if (!seen.Add(proteinId))
            {
                // Duplicate ids are silently deduplicated, preserving the first
                // occurrence and the original index for any error key.
                continue;
            }

            if (!byId.TryGetValue(proteinId, out var protein))
            {
                errors[$"protein_ids[{index}]"] = ["protein_id must reference an existing protein."];
                continue;
            }

            if (!protein.IsActive && !existing.ProteinIds.Contains(protein.Id))
            {
                errors[$"protein_ids[{index}]"] =
                    ["A retired protein cannot be newly selected; only existing references may be kept."];
                continue;
            }

            resolved.Add(protein.Id);
        }

        return resolved;
    }

    private async Task<List<long>> ResolveTagsAsync(
        IReadOnlyList<long> tagIds,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (tagIds.Count == 0)
        {
            return [];
        }

        var distinct = tagIds.Distinct().ToArray();
        var existingIds = await dbContext.Tags.AsNoTracking()
            .Where(tag => distinct.Contains(tag.Id))
            .Select(tag => tag.Id)
            .ToListAsync(cancellationToken);
        var known = existingIds.ToHashSet();

        var resolved = new List<long>();
        var seen = new HashSet<long>();
        for (var index = 0; index < tagIds.Count; index++)
        {
            var tagId = tagIds[index];
            if (!seen.Add(tagId))
            {
                // Duplicate ids are silently deduplicated (see proteins above).
                continue;
            }

            if (known.Contains(tagId))
            {
                resolved.Add(tagId);
            }
            else
            {
                errors[$"tag_ids[{index}]"] = ["tag_id must reference an existing tag."];
            }
        }

        return resolved;
    }

    /// <summary>
    /// Inserts the resolved nested slices in dependency order (groups and
    /// sections before their children) so generated ids are available.
    /// </summary>
    private async Task InsertNestedAsync(
        long recipeId,
        ResolvedNested nested,
        CancellationToken cancellationToken)
    {
        var groupRows = new List<RecipeIngredientGroup>(nested.Groups.Count);
        for (var groupIndex = 0; groupIndex < nested.Groups.Count; groupIndex++)
        {
            var group = new RecipeIngredientGroup
            {
                RecipeId = recipeId,
                Title = nested.Groups[groupIndex].Title,
                SortOrder = groupIndex,
            };
            groupRows.Add(group);
            dbContext.RecipeIngredientGroups.Add(group);
        }

        var sectionRows = new List<RecipeInstructionSection>(nested.Sections.Count);
        for (var sectionIndex = 0; sectionIndex < nested.Sections.Count; sectionIndex++)
        {
            var section = new RecipeInstructionSection
            {
                RecipeId = recipeId,
                Title = nested.Sections[sectionIndex].Title,
                SortOrder = sectionIndex,
            };
            sectionRows.Add(section);
            dbContext.RecipeInstructionSections.Add(section);
        }

        if (groupRows.Count > 0 || sectionRows.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        for (var groupIndex = 0; groupIndex < groupRows.Count; groupIndex++)
        {
            AddIngredientRows(recipeId, groupRows[groupIndex].Id, nested.Groups[groupIndex].Ingredients);
        }

        AddIngredientRows(recipeId, groupId: null, nested.Ungrouped);

        for (var sectionIndex = 0; sectionIndex < sectionRows.Count; sectionIndex++)
        {
            var steps = nested.Sections[sectionIndex].Steps;
            for (var stepIndex = 0; stepIndex < steps.Count; stepIndex++)
            {
                dbContext.RecipeInstructionSteps.Add(new RecipeInstructionStep
                {
                    SectionId = sectionRows[sectionIndex].Id,
                    Text = steps[stepIndex],
                    SortOrder = stepIndex,
                });
            }
        }

        for (var index = 0; index < nested.ProteinIds.Count; index++)
        {
            dbContext.RecipeProteins.Add(new RecipeProtein
            {
                RecipeId = recipeId,
                ProteinId = nested.ProteinIds[index],
            });
        }

        for (var index = 0; index < nested.TagIds.Count; index++)
        {
            dbContext.RecipeTags.Add(new RecipeTag
            {
                RecipeId = recipeId,
                TagId = nested.TagIds[index],
            });
        }

        // Flush the children before the caller reads the resulting document for
        // the audit snapshot: LoadNestedAsync queries the database, so pending
        // entities would otherwise be missing from the recorded after-image.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddIngredientRows(
        long recipeId,
        long? groupId,
        IReadOnlyList<ResolvedIngredient> resolved)
    {
        for (var index = 0; index < resolved.Count; index++)
        {
            var item = resolved[index];
            dbContext.RecipeIngredients.Add(new RecipeIngredient
            {
                RecipeId = recipeId,
                GroupId = groupId,
                IngredientId = item.Ingredient.Id,
                QuantityNumeric = item.Write.Quantity,
                UnitId = item.Unit?.Id,
                PreparationNote = item.Write.PreparationNote,
                IsOptional = item.Write.IsOptional,
                SortOrder = index,
            });
        }
    }

    private async Task<RecipeImageRecord?> LoadImageAsync(
        long recipeId,
        CancellationToken cancellationToken)
    {
        var image = await dbContext.RecipeImages.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.RecipeId == recipeId, cancellationToken);
        return image is null ? null : ToImageRecord(image);
    }

    private async Task<Dictionary<long, RecipeImageRecord>> LoadImagesAsync(
        long[] recipeIds,
        CancellationToken cancellationToken)
    {
        if (recipeIds.Length == 0)
        {
            return [];
        }

        var images = await dbContext.RecipeImages.AsNoTracking()
            .Where(image => recipeIds.Contains(image.RecipeId))
            .ToListAsync(cancellationToken);

        return images.ToDictionary(image => image.RecipeId, ToImageRecord);
    }

    private async Task<RecipeNested> LoadNestedAsync(long recipeId, CancellationToken cancellationToken)
    {
        var groups = await dbContext.RecipeIngredientGroups.AsNoTracking()
            .Where(group => group.RecipeId == recipeId)
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Id)
            .ToListAsync(cancellationToken);

        var ingredientRows = await dbContext.RecipeIngredients.AsNoTracking()
            .Where(ingredient => ingredient.RecipeId == recipeId)
            .OrderBy(ingredient => ingredient.SortOrder)
            .ThenBy(ingredient => ingredient.Id)
            .ToListAsync(cancellationToken);

        var ingredientRefs = await LoadIngredientReferencesAsync(
            ingredientRows.Select(ingredient => ingredient.IngredientId).Distinct().ToArray(),
            ingredientRows
                .Where(ingredient => ingredient.UnitId is not null)
                .Select(ingredient => ingredient.UnitId!.Value)
                .Distinct()
                .ToArray(),
            cancellationToken);

        RecipeIngredientRecord ToIngredient(RecipeIngredient ingredient) =>
            new(
                ingredient.Id,
                ingredientRefs.Ingredients[ingredient.IngredientId],
                ingredient.QuantityNumeric,
                ingredient.UnitId is { } unitId ? ingredientRefs.Units[unitId] : null,
                ingredient.PreparationNote,
                ingredient.IsOptional,
                ingredient.SortOrder);

        var ingredientRecords = ingredientRows.Select(ToIngredient).ToArray();
        var ungrouped = ingredientRecords
            .Where((_, index) => ingredientRows[index].GroupId is null)
            .ToArray();

        var groupRecords = groups
            .Select(group => new RecipeIngredientGroupRecord(
                group.Id,
                group.Title,
                group.SortOrder,
                ingredientRows
                    .Select((row, index) => (row, index))
                    .Where(pair => pair.row.GroupId == group.Id)
                    .OrderBy(pair => pair.row.SortOrder)
                    .ThenBy(pair => pair.row.Id)
                    .Select(pair => ingredientRecords[pair.index])
                    .ToArray()))
            .ToArray();

        var sections = await dbContext.RecipeInstructionSections.AsNoTracking()
            .Where(section => section.RecipeId == recipeId)
            .OrderBy(section => section.SortOrder)
            .ThenBy(section => section.Id)
            .ToListAsync(cancellationToken);
        var sectionIds = sections.Select(section => section.Id).ToArray();
        var steps = sectionIds.Length == 0
            ? []
            : await dbContext.RecipeInstructionSteps.AsNoTracking()
                .Where(step => sectionIds.Contains(step.SectionId))
                .OrderBy(step => step.SortOrder)
                .ThenBy(step => step.Id)
                .ToListAsync(cancellationToken);

        var sectionRecords = sections
            .Select(section => new RecipeInstructionSectionRecord(
                section.Id,
                section.Title,
                section.SortOrder,
                steps
                    .Where(step => step.SectionId == section.Id)
                    .OrderBy(step => step.SortOrder)
                    .ThenBy(step => step.Id)
                    .Select(step => new RecipeInstructionStepRecord(step.Id, step.Text, step.SortOrder))
                    .ToArray()))
            .ToArray();

        var proteins = await dbContext.RecipeProteins.AsNoTracking()
            .Where(link => link.RecipeId == recipeId)
            .Join(
                dbContext.Proteins.AsNoTracking(),
                link => link.ProteinId,
                protein => protein.Id,
                (_, protein) => protein)
            .OrderBy(protein => protein.Name)
            .ThenBy(protein => protein.Id)
            .Select(protein => new RecipeProteinRecord(protein.Id, protein.Name, protein.IsActive))
            .ToListAsync(cancellationToken);

        var tags = await dbContext.RecipeTags.AsNoTracking()
            .Where(link => link.RecipeId == recipeId)
            .Join(
                dbContext.Tags.AsNoTracking(),
                link => link.TagId,
                tag => tag.Id,
                (_, tag) => tag)
            .OrderBy(tag => tag.Name)
            .ThenBy(tag => tag.Id)
            .Select(tag => new RecipeTagRecord(tag.Id, tag.Name))
            .ToListAsync(cancellationToken);

        return new RecipeNested(ungrouped, groupRecords, sectionRecords, proteins, tags);
    }

    private sealed record RecipeNested(
        IReadOnlyList<RecipeIngredientRecord> Ingredients,
        IReadOnlyList<RecipeIngredientGroupRecord> IngredientGroups,
        IReadOnlyList<RecipeInstructionSectionRecord> InstructionSections,
        IReadOnlyList<RecipeProteinRecord> Proteins,
        IReadOnlyList<RecipeTagRecord> Tags)
    {
        public static RecipeNested Empty =>
            new([], [], [], [], []);
    }

    private sealed record IngredientReferenceMaps(
        Dictionary<long, RecipeIngredientReference> Ingredients,
        Dictionary<long, RecipeUnitReference> Units);

    private async Task<IngredientReferenceMaps> LoadIngredientReferencesAsync(
        long[] ingredientIds,
        long[] unitIds,
        CancellationToken cancellationToken)
    {
        var ingredients = ingredientIds.Length == 0
            ? []
            : (await dbContext.Ingredients.AsNoTracking()
                    .Where(ingredient => ingredientIds.Contains(ingredient.Id))
                    .ToListAsync(cancellationToken))
                .ToDictionary(
                    ingredient => ingredient.Id,
                    ingredient => new RecipeIngredientReference(
                        ingredient.Id,
                        ingredient.CanonicalName,
                        ingredient.IsActive));
        var units = unitIds.Length == 0
            ? []
            : (await dbContext.Units.AsNoTracking()
                    .Where(unit => unitIds.Contains(unit.Id))
                    .ToListAsync(cancellationToken))
                .ToDictionary(
                    unit => unit.Id,
                    unit => new RecipeUnitReference(unit.Id, unit.Symbol, unit.DisplayName, unit.IsActive));
        return new IngredientReferenceMaps(ingredients, units);
    }

    private static RecipeImageRecord ToImageRecord(RecipeImage image) =>
        new(image.Id, image.AltText, image.SourceUrl, image.CreatedAt, image.StoragePath);

    private static string SnapshotJson(Recipe recipe, RecipeNested nested) =>
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
            ingredient_groups = nested.IngredientGroups.Select(group => new
            {
                title = group.Title,
                sort_order = group.SortOrder,
                ingredients = group.Ingredients.Select(IngredientSnapshot).ToArray(),
            }).ToArray(),
            ingredients = nested.Ingredients.Select(IngredientSnapshot).ToArray(),
            instruction_sections = nested.InstructionSections.Select(section => new
            {
                title = section.Title,
                sort_order = section.SortOrder,
                steps = section.Steps.Select(step => new
                {
                    text = step.Text,
                    sort_order = step.SortOrder,
                }).ToArray(),
            }).ToArray(),
            proteins = nested.Proteins.Select(protein => new
            {
                id = protein.Id,
                name = protein.Name,
                is_active = protein.IsActive,
            }).ToArray(),
            tags = nested.Tags.Select(tag => new
            {
                id = tag.Id,
                name = tag.Name,
            }).ToArray(),
        });

    private static object IngredientSnapshot(RecipeIngredientRecord ingredient) => new
    {
        ingredient_id = ingredient.Ingredient.Id,
        ingredient_name = ingredient.Ingredient.Name,
        ingredient_is_active = ingredient.Ingredient.IsActive,
        quantity = ingredient.Quantity,
        unit_id = ingredient.Unit?.Id,
        unit_symbol = ingredient.Unit?.Symbol,
        unit_is_active = ingredient.Unit?.IsActive,
        preparation_note = ingredient.PreparationNote,
        is_optional = ingredient.IsOptional,
        sort_order = ingredient.SortOrder,
    };

    private static RecipeRecord ToRecord(
        Recipe recipe,
        RecipeImageRecord? image,
        RecipeNested? nested = null)
    {
        var slices = nested ?? RecipeNested.Empty;
        return new(
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
            recipe.UpdatedAt,
            image,
            slices.IngredientGroups,
            slices.Ingredients,
            slices.InstructionSections,
            slices.Proteins,
            slices.Tags);
    }

    private sealed record MetadataValues(
        string? Description,
        string? SourceSite,
        string? SourceUrl,
        string? NormalizedSourceUrl,
        string? Author,
        int? PrepMinutes,
        int? CookMinutes,
        string? Notes,
        RecipeStatus? Status);
}
