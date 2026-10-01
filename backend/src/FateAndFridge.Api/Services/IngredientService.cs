using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>One ingredient alias as exposed by the API and by audit snapshots.</summary>
public sealed record IngredientAliasRecord(long Id, string Alias);

/// <summary>One ingredient as exposed by the API and by audit snapshots.</summary>
public sealed record IngredientRecord(
    long Id,
    string CanonicalName,
    long? DepartmentId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<IngredientAliasRecord> Aliases);

/// <summary>Outcome of an ingredient create/update/retire/delete attempt.</summary>
public abstract record IngredientMutationResult
{
    private IngredientMutationResult()
    {
    }

    public sealed record Success(IngredientRecord Ingredient) : IngredientMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : IngredientMutationResult;

    public sealed record NotFound : IngredientMutationResult;

    /// <summary>
    /// The canonical name collides with another ingredient's canonical name or
    /// alias in the shared ingredient namespace.
    /// </summary>
    public sealed record Conflict(long? ExistingId) : IngredientMutationResult;

    /// <summary>
    /// An alias supplied at create time collides; the whole create is rejected
    /// atomically with the alias-specific details.
    /// </summary>
    public sealed record AliasConflict(IReadOnlyDictionary<string, object?> Details) : IngredientMutationResult;

    /// <summary>
    /// The ingredient is still referenced by recipe data (or is the target of a
    /// merge, reported with <see cref="Reason"/>).
    /// </summary>
    public sealed record InUse(int UsageCount, string? Reason = null) : IngredientMutationResult;
}

/// <summary>Outcome of an ingredient alias add/remove attempt.</summary>
public abstract record IngredientAliasMutationResult
{
    private IngredientAliasMutationResult()
    {
    }

    public sealed record Success(IngredientAliasRecord Alias) : IngredientAliasMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : IngredientAliasMutationResult;

    public sealed record NotFound : IngredientAliasMutationResult;

    /// <summary>
    /// The alias matches an ingredient canonical name or an existing alias
    /// (own or other) in the shared ingredient namespace.
    /// </summary>
    public sealed record AliasConflict(IReadOnlyDictionary<string, object?> Details) : IngredientAliasMutationResult;
}

/// <summary>
/// M2-08 ingredients management (docs/API.md §22, docs/DATABASE.md §4):
/// searchable reference data whose canonical names and aliases share one global
/// uniqueness namespace, an optional active department, per-ingredient aliases,
/// retire/delete semantics, and an audit row staged into the caller's
/// <see cref="AppDbContext"/> for every write.
/// </summary>
public sealed class IngredientService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IAuditService auditService,
    IReferenceUsageChecker usageChecker,
    ILogger<IngredientService> logger)
{
    private const string DepartmentMessage = "department_id must reference an active department.";

    private const string DuplicateAliasMessage = "aliases must not repeat the same normalized text.";

    private const string MergeReferenceReason = "merge_reference";

    /// <summary>
    /// Lists ingredients ordered by id, prefix matches first when searching. The
    /// search matches the canonical name and alias text case-insensitively;
    /// retired ingredients are excluded unless requested.
    /// </summary>
    public async Task<IReadOnlyList<IngredientRecord>> ListAsync(
        string? normalizedSearch,
        int limit,
        bool includeRetired,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Ingredients.AsNoTracking();
        if (!includeRetired)
        {
            query = query.Where(ingredient => ingredient.IsActive);
        }

        if (normalizedSearch is not null)
        {
            query = query.Where(ingredient =>
                ingredient.NormalizedName.Contains(normalizedSearch)
                || dbContext.IngredientAliases.Any(
                    alias => alias.IngredientId == ingredient.Id
                        && alias.NormalizedAlias.Contains(normalizedSearch)));
        }

        var ingredients = await query.ToListAsync(cancellationToken);
        var ingredientIds = ingredients.Select(ingredient => ingredient.Id).ToArray();
        var aliases = await dbContext.IngredientAliases
            .AsNoTracking()
            .Where(alias => ingredientIds.Contains(alias.IngredientId))
            .OrderBy(alias => alias.Id)
            .ToListAsync(cancellationToken);
        var aliasesByIngredient = aliases
            .GroupBy(alias => alias.IngredientId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return ingredients
            .OrderBy(ingredient => IsPrefixMatch(ingredient, aliasesByIngredient, normalizedSearch) ? 0 : 1)
            .ThenBy(ingredient => ingredient.Id)
            .Take(limit)
            .Select(ingredient => ToRecord(ingredient, AliasesFor(ingredient.Id, aliasesByIngredient)))
            .ToArray();
    }

    /// <summary>
    /// Creates an ingredient with optional active-department link and aliases.
    /// The canonical name and every alias are checked against the shared
    /// ingredient namespace before anything is written, and the create, its
    /// aliases, and its audit row commit atomically.
    /// </summary>
    public async Task<IngredientMutationResult> CreateAsync(
        string? canonicalName,
        long? departmentId,
        IReadOnlyList<string>? aliases,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var normalizedName = NormalizeRequired(canonicalName, "canonical_name", errors);
        await ValidateDepartmentAsync(departmentId, errors, cancellationToken);
        var normalizedAliases = NormalizeAliases(aliases, errors);
        if (errors.Count > 0)
        {
            return new IngredientMutationResult.ValidationFailed(errors);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var nameConflict = await FindUniquenessConflictAsync(
            normalizedName!.Normalized,
            excludingIngredientId: null,
            cancellationToken);
        if (nameConflict is not null)
        {
            return new IngredientMutationResult.Conflict(nameConflict.IngredientId);
        }

        foreach (var alias in normalizedAliases)
        {
            // The new canonical name is not in the database yet; treat a
            // matching alias as a self canonical-name conflict.
            if (string.Equals(alias.Normalized, normalizedName.Normalized, StringComparison.Ordinal))
            {
                return new IngredientMutationResult.AliasConflict(
                    AliasConflictDetails(ingredientId: null, "canonical_name", aliasId: null));
            }

            var aliasConflict = await FindUniquenessConflictAsync(
                alias.Normalized,
                excludingIngredientId: null,
                cancellationToken);
            if (aliasConflict is not null)
            {
                return new IngredientMutationResult.AliasConflict(
                    AliasConflictDetails(aliasConflict.IngredientId, aliasConflict.Source, aliasConflict.AliasId));
            }
        }

        var now = timeProvider.GetUtcNow();
        var ingredient = new Ingredient
        {
            CanonicalName = normalizedName.Display,
            NormalizedName = normalizedName.Normalized,
            DepartmentId = departmentId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Ingredients.Add(ingredient);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            var race = await FindUniquenessConflictAsync(
                normalizedName.Normalized,
                excludingIngredientId: null,
                cancellationToken);
            return new IngredientMutationResult.Conflict(race?.IngredientId);
        }

        var aliasRows = normalizedAliases
            .Select(alias => new IngredientAlias
            {
                IngredientId = ingredient.Id,
                Alias = alias.Display,
                NormalizedAlias = alias.Normalized,
            })
            .ToArray();
        dbContext.IngredientAliases.AddRange(aliasRows);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Created,
                AuditEntityTypes.Ingredient,
                EntityId: ingredient.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(ingredient, aliasRows),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost an alias race; the canonical name already checked out, so the
            // collision is alias-specific. Nothing from this create commits.
            dbContext.ChangeTracker.Clear();
            foreach (var alias in normalizedAliases)
            {
                var race = await FindUniquenessConflictAsync(alias.Normalized, null, cancellationToken);
                if (race is not null)
                {
                    return new IngredientMutationResult.AliasConflict(
                        AliasConflictDetails(race.IngredientId, race.Source, race.AliasId));
                }
            }

            return new IngredientMutationResult.Conflict(null);
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created ingredient {IngredientId}", ingredient.Id);
        return new IngredientMutationResult.Success(ToRecord(ingredient, aliasRows));
    }

    /// <summary>
    /// Renames the canonical name and/or sets or clears the department link.
    /// An explicit null department clears it; an absent property leaves it.
    /// Renames are checked against the shared namespace, excluding the
    /// ingredient itself.
    /// </summary>
    public async Task<IngredientMutationResult> UpdateAsync(
        long id,
        string? canonicalName,
        bool canonicalRequested,
        long? departmentId,
        bool departmentRequested,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var ingredient = await dbContext.Ingredients.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (ingredient is null)
        {
            return new IngredientMutationResult.NotFound();
        }

        var aliases = await LoadAliasesAsync(id, cancellationToken);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var normalizedName = canonicalRequested
            ? NormalizeRequired(canonicalName, "canonical_name", errors)
            : null;
        if (departmentRequested)
        {
            await ValidateDepartmentAsync(departmentId, errors, cancellationToken);
        }

        if (!canonicalRequested && !departmentRequested)
        {
            errors["body"] = ["At least one of canonical_name or department_id must be provided."];
        }

        if (errors.Count > 0)
        {
            return new IngredientMutationResult.ValidationFailed(errors);
        }

        await using var transaction = normalizedName is not null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (normalizedName is not null)
        {
            var conflict = await FindUniquenessConflictAsync(
                normalizedName.Normalized,
                excludingIngredientId: id,
                cancellationToken);
            if (conflict is not null)
            {
                return new IngredientMutationResult.Conflict(conflict.IngredientId);
            }
        }

        var before = SnapshotJson(ingredient, aliases);
        if (normalizedName is not null)
        {
            ingredient.CanonicalName = normalizedName.Display;
            ingredient.NormalizedName = normalizedName.Normalized;
        }

        if (departmentRequested)
        {
            ingredient.DepartmentId = departmentId;
        }

        ingredient.UpdatedAt = timeProvider.GetUtcNow();

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Ingredient,
                EntityId: ingredient.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(ingredient, aliases),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            return new IngredientMutationResult.Conflict(
                normalizedName is null
                    ? null
                    : (await FindUniquenessConflictAsync(normalizedName.Normalized, id, cancellationToken))?.IngredientId);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Updated ingredient {IngredientId}", id);
        return new IngredientMutationResult.Success(ToRecord(ingredient, aliases));
    }

    /// <summary>
    /// Adds an alias. The alias must be unique across every canonical name and
    /// every alias in the shared ingredient namespace.
    /// </summary>
    public async Task<IngredientAliasMutationResult> AddAliasAsync(
        long ingredientId,
        string? alias,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var ingredient = await dbContext.Ingredients.SingleOrDefaultAsync(
            candidate => candidate.Id == ingredientId,
            cancellationToken);
        if (ingredient is null)
        {
            return new IngredientAliasMutationResult.NotFound();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var normalized = NormalizeRequired(alias, "alias", errors);
        if (normalized is null)
        {
            return new IngredientAliasMutationResult.ValidationFailed(errors);
        }

        var aliases = await LoadAliasesAsync(ingredientId, cancellationToken);
        var duplicate = aliases.FirstOrDefault(
            candidate => candidate.NormalizedAlias == normalized.Normalized);
        if (duplicate is not null)
        {
            return new IngredientAliasMutationResult.AliasConflict(
                AliasConflictDetails(ingredientId, "alias", duplicate.Id));
        }

        var conflict = await FindUniquenessConflictAsync(
            normalized.Normalized,
            excludingIngredientId: null,
            cancellationToken);
        if (conflict is not null)
        {
            return new IngredientAliasMutationResult.AliasConflict(
                AliasConflictDetails(conflict.IngredientId, conflict.Source, conflict.AliasId));
        }

        var before = SnapshotJson(ingredient, aliases);
        var row = new IngredientAlias
        {
            IngredientId = ingredientId,
            Alias = normalized.Display,
            NormalizedAlias = normalized.Normalized,
        };

        dbContext.IngredientAliases.Add(row);
        ingredient.UpdatedAt = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            var race = await FindUniquenessConflictAsync(normalized.Normalized, null, cancellationToken);
            return new IngredientAliasMutationResult.AliasConflict(
                race is null
                    ? AliasConflictDetails(ingredientId, "alias", aliasId: null)
                    : AliasConflictDetails(race.IngredientId, race.Source, race.AliasId));
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Ingredient,
                EntityId: ingredient.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(ingredient, [.. aliases, row]),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Added alias {AliasId} to ingredient {IngredientId}", row.Id, ingredientId);
        return new IngredientAliasMutationResult.Success(new IngredientAliasRecord(row.Id, row.Alias));
    }

    /// <summary>
    /// Removes one alias from an ingredient. An id that belongs to another
    /// ingredient (or to no ingredient at all) is reported as not found.
    /// </summary>
    public async Task<IngredientAliasMutationResult> RemoveAliasAsync(
        long ingredientId,
        long aliasId,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var ingredient = await dbContext.Ingredients.SingleOrDefaultAsync(
            candidate => candidate.Id == ingredientId,
            cancellationToken);
        if (ingredient is null)
        {
            return new IngredientAliasMutationResult.NotFound();
        }

        var aliases = await LoadAliasesAsync(ingredientId, cancellationToken);
        var alias = aliases.FirstOrDefault(candidate => candidate.Id == aliasId);
        if (alias is null)
        {
            return new IngredientAliasMutationResult.NotFound();
        }

        var before = SnapshotJson(ingredient, aliases);
        dbContext.IngredientAliases.Remove(alias);
        ingredient.UpdatedAt = timeProvider.GetUtcNow();

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Ingredient,
                EntityId: ingredient.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(ingredient, aliases.Where(candidate => candidate.Id != aliasId)),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Removed alias {AliasId} from ingredient {IngredientId}", aliasId, ingredientId);
        return new IngredientAliasMutationResult.Success(new IngredientAliasRecord(alias.Id, alias.Alias));
    }

    /// <summary>Deactivates an ingredient; repeating the call is a no-op and writes no audit row.</summary>
    public async Task<IngredientMutationResult> RetireAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var ingredient = await dbContext.Ingredients.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (ingredient is null)
        {
            return new IngredientMutationResult.NotFound();
        }

        var aliases = await LoadAliasesAsync(id, cancellationToken);
        if (!ingredient.IsActive)
        {
            return new IngredientMutationResult.Success(ToRecord(ingredient, aliases));
        }

        var before = SnapshotJson(ingredient, aliases);
        ingredient.IsActive = false;
        ingredient.UpdatedAt = timeProvider.GetUtcNow();

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Retired,
                AuditEntityTypes.Ingredient,
                EntityId: ingredient.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(ingredient, aliases),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Retired ingredient {IngredientId}", id);
        return new IngredientMutationResult.Success(ToRecord(ingredient, aliases));
    }

    /// <summary>
    /// Hard-deletes an ingredient only while no recipe data references it
    /// (docs/API.md §22). The count comes from the usage seam; a merge
    /// reference blocked by the database FK is reported as in use with the
    /// <c>merge_reference</c> reason. Aliases cascade with the row.
    /// </summary>
    public async Task<IngredientMutationResult> DeleteAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var ingredient = await dbContext.Ingredients.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (ingredient is null)
        {
            return new IngredientMutationResult.NotFound();
        }

        var usageCount = await usageChecker.CountIngredientReferencesAsync(id, cancellationToken);
        if (usageCount > 0)
        {
            return new IngredientMutationResult.InUse(usageCount);
        }

        var aliases = await LoadAliasesAsync(id, cancellationToken);
        var before = SnapshotJson(ingredient, aliases);
        dbContext.Ingredients.Remove(ingredient);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Deleted,
                AuditEntityTypes.Ingredient,
                EntityId: ingredient.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsForeignKeyViolation(exception))
        {
            // Another ingredient still merges into this one (RESTRICT); the
            // usage seam cannot see that yet, so surface it as in-use.
            logger.LogInformation(
                "Delete of ingredient {IngredientId} blocked by a merge reference",
                id);
            return new IngredientMutationResult.InUse(0, MergeReferenceReason);
        }

        logger.LogInformation("Deleted ingredient {IngredientId}", id);
        return new IngredientMutationResult.Success(ToRecord(ingredient, []));
    }

    private async Task ValidateDepartmentAsync(
        long? departmentId,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (departmentId is null)
        {
            return;
        }

        var exists = await dbContext.GroceryDepartments
            .AsNoTracking()
            .AnyAsync(
                department => department.Id == departmentId.Value && department.IsActive,
                cancellationToken);
        if (!exists)
        {
            errors["department_id"] = [DepartmentMessage];
        }
    }

    private List<ReferenceDataNormalizationResult.Success> NormalizeAliases(
        IReadOnlyList<string>? aliases,
        Dictionary<string, string[]> errors)
    {
        var normalized = new List<ReferenceDataNormalizationResult.Success>();
        if (aliases is null)
        {
            return normalized;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            var normalization = ReferenceDataNormalizer.Normalize(alias);
            if (normalization is not ReferenceDataNormalizationResult.Success success)
            {
                errors["aliases"] = [AliasErrorMessage(normalization)];
                continue;
            }

            if (!seen.Add(success.Normalized))
            {
                errors["aliases"] = [DuplicateAliasMessage];
                continue;
            }

            normalized.Add(success);
        }

        return normalized;
    }

    private static ReferenceDataNormalizationResult.Success? NormalizeRequired(
        string? value,
        string fieldName,
        Dictionary<string, string[]> errors)
    {
        var normalization = ReferenceDataNormalizer.Normalize(value);
        if (normalization is ReferenceDataNormalizationResult.Success success)
        {
            return success;
        }

        errors[fieldName] = [FieldErrorMessage(fieldName, normalization)];
        return null;
    }

    private static string FieldErrorMessage(
        string fieldName,
        ReferenceDataNormalizationResult normalization) =>
        normalization is ReferenceDataNormalizationResult.Invalid
        {
            Reason: ReferenceDataNormalizationFailureReason.ControlCharacters,
        }
            ? $"{fieldName} contains unsupported control characters."
            : $"{fieldName} is required and must not be blank.";

    private static string AliasErrorMessage(ReferenceDataNormalizationResult normalization) =>
        normalization is ReferenceDataNormalizationResult.Invalid
        {
            Reason: ReferenceDataNormalizationFailureReason.ControlCharacters,
        }
            ? "aliases contains unsupported control characters."
            : "aliases must not contain blank values.";

    /// <summary>
    /// Looks up one normalized value in the shared ingredient namespace. The
    /// owning ingredient id is returned for both canonical-name and alias hits,
    /// with the alias row id when the hit is an alias.
    /// </summary>
    private async Task<UniquenessConflict?> FindUniquenessConflictAsync(
        string normalized,
        long? excludingIngredientId,
        CancellationToken cancellationToken)
    {
        var nameOwnerId = await dbContext.Ingredients
            .AsNoTracking()
            .Where(ingredient => ingredient.NormalizedName == normalized)
            .Where(ingredient => excludingIngredientId == null || ingredient.Id != excludingIngredientId.Value)
            .OrderBy(ingredient => ingredient.Id)
            .Select(ingredient => (long?)ingredient.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (nameOwnerId is not null)
        {
            return new UniquenessConflict(nameOwnerId.Value, "canonical_name", AliasId: null);
        }

        var aliasOwner = await dbContext.IngredientAliases
            .AsNoTracking()
            .Where(alias => alias.NormalizedAlias == normalized)
            .Where(alias => excludingIngredientId == null || alias.IngredientId != excludingIngredientId.Value)
            .OrderBy(alias => alias.Id)
            .Select(alias => new { alias.Id, alias.IngredientId })
            .FirstOrDefaultAsync(cancellationToken);
        return aliasOwner is null
            ? null
            : new UniquenessConflict(aliasOwner.IngredientId, "alias", aliasOwner.Id);
    }

    private Task<List<IngredientAlias>> LoadAliasesAsync(long ingredientId, CancellationToken cancellationToken) =>
        dbContext.IngredientAliases
            .Where(alias => alias.IngredientId == ingredientId)
            .OrderBy(alias => alias.Id)
            .ToListAsync(cancellationToken);

    private static IReadOnlyDictionary<string, object?> AliasConflictDetails(
        long? ingredientId,
        string source,
        long? aliasId)
    {
        var details = new Dictionary<string, object?>
        {
            ["entity_type"] = AuditEntityTypes.Ingredient,
            ["source"] = source,
        };
        if (ingredientId is not null)
        {
            details["existing_id"] = ingredientId.Value;
        }

        if (aliasId is not null)
        {
            details["alias_id"] = aliasId.Value;
        }

        return details;
    }

    private static bool IsPrefixMatch(
        Ingredient ingredient,
        IReadOnlyDictionary<long, IngredientAlias[]> aliasesByIngredient,
        string? normalizedSearch)
    {
        if (normalizedSearch is null)
        {
            return true;
        }

        if (ingredient.NormalizedName.StartsWith(normalizedSearch, StringComparison.Ordinal))
        {
            return true;
        }

        return aliasesByIngredient.TryGetValue(ingredient.Id, out var aliases)
            && aliases.Any(alias => alias.NormalizedAlias.StartsWith(normalizedSearch, StringComparison.Ordinal));
    }

    private static IngredientAlias[] AliasesFor(
        long ingredientId,
        IReadOnlyDictionary<long, IngredientAlias[]> aliasesByIngredient) =>
        aliasesByIngredient.TryGetValue(ingredientId, out var aliases) ? aliases : [];

    private static string SnapshotJson(Ingredient ingredient, IEnumerable<IngredientAlias> aliases) =>
        JsonSerializer.Serialize(new
        {
            id = ingredient.Id,
            canonical_name = ingredient.CanonicalName,
            normalized_name = ingredient.NormalizedName,
            department_id = ingredient.DepartmentId,
            merged_into_ingredient_id = ingredient.MergedIntoIngredientId,
            is_active = ingredient.IsActive,
            created_at = ingredient.CreatedAt,
            updated_at = ingredient.UpdatedAt,
            aliases = aliases
                .OrderBy(alias => alias.Id)
                .Select(alias => new
                {
                    id = alias.Id,
                    alias = alias.Alias,
                    normalized_alias = alias.NormalizedAlias,
                })
                .ToArray(),
        });

    private static IngredientRecord ToRecord(Ingredient ingredient, IReadOnlyCollection<IngredientAlias> aliases) =>
        new(
            ingredient.Id,
            ingredient.CanonicalName,
            ingredient.DepartmentId,
            ingredient.IsActive,
            ingredient.CreatedAt,
            ingredient.UpdatedAt,
            aliases
                .Where(alias => alias.IngredientId == ingredient.Id)
                .OrderBy(alias => alias.Id)
                .Select(alias => new IngredientAliasRecord(alias.Id, alias.Alias))
                .ToArray());

    private sealed record UniquenessConflict(long IngredientId, string Source, long? AliasId);
}
