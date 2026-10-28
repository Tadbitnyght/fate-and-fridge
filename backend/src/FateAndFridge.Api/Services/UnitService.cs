using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>One unit alias as exposed by the API and by audit snapshots.</summary>
public sealed record UnitAliasRecord(long Id, string Alias);

/// <summary>One unit as exposed by the API and by audit snapshots.</summary>
public sealed record UnitRecord(
    long Id,
    string Symbol,
    string DisplayName,
    UnitDimension Dimension,
    decimal? BaseFactor,
    bool IsCustom,
    bool IsActive,
    IReadOnlyList<UnitAliasRecord> Aliases);

/// <summary>Outcome of a unit create/update/retire/delete attempt.</summary>
public abstract record UnitMutationResult
{
    private UnitMutationResult()
    {
    }

    public sealed record Success(UnitRecord Unit) : UnitMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : UnitMutationResult;

    public sealed record NotFound : UnitMutationResult;

    /// <summary>A symbol or normalized display name is already taken; the owner id is included when known.</summary>
    public sealed record Conflict(long? ExistingId) : UnitMutationResult;

    /// <summary>The unit is still referenced by recipe ingredients; retire it instead.</summary>
    public sealed record InUse(int UsageCount) : UnitMutationResult;
}

/// <summary>Outcome of a unit alias add/remove attempt.</summary>
public abstract record UnitAliasMutationResult
{
    private UnitAliasMutationResult()
    {
    }

    public sealed record Success(UnitAliasRecord Alias) : UnitAliasMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : UnitAliasMutationResult;

    public sealed record NotFound : UnitAliasMutationResult;

    /// <summary>The alias is already used by this unit (an existing alias row, or the unit's own symbol/display name).</summary>
    public sealed record AliasConflict(IReadOnlyDictionary<string, object?> Details) : UnitAliasMutationResult;
}

/// <summary>
/// Units management (docs/API.md §22, docs/DATABASE.md §5): searchable
/// reference data with an immutable dimension, base factors editable only for
/// mass/volume units (bounds-checked and audited as <c>factor_changed</c>)
/// per-unit aliases, retire/delete semantics, and an audit row staged into the
/// caller's <see cref="AppDbContext"/> for every write.
/// </summary>
public sealed class UnitService(
    AppDbContext dbContext,
    IAuditService auditService,
    IReferenceUsageChecker usageChecker,
    ILogger<UnitService> logger)
{
    /// <summary>Inclusive lower bound for an editable <c>base_factor</c>.</summary>
    public const decimal MinBaseFactor = 0.000001m;

    /// <summary>Inclusive upper bound for an editable <c>base_factor</c>.</summary>
    public const decimal MaxBaseFactor = 1000000m;

    private const string FactorRequiredMessage = "base_factor is required for mass and volume units.";

    private const string FactorForbiddenMessage = "base_factor is not allowed for count and nonconvertible units.";

    private const string FactorBoundsMessage =
        "base_factor must be between 0.000001 and 1000000 (inclusive).";

    private const string FactorNotEditableMessage = "base_factor is only editable for mass and volume units.";

    /// <summary>
    /// Lists units ordered by id, prefix matches first when searching. The
    /// search matches the symbol, the normalized display name, and alias text
    /// case-insensitively; retired units are excluded unless requested.
    /// </summary>
    public async Task<IReadOnlyList<UnitRecord>> ListAsync(
        string? normalizedSearch,
        int limit,
        bool includeRetired,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Units.AsNoTracking();
        if (!includeRetired)
        {
            query = query.Where(unit => unit.IsActive);
        }

        if (normalizedSearch is not null)
        {
            query = query.Where(unit =>
                unit.NormalizedName.Contains(normalizedSearch)
                || unit.Symbol.ToLower().Contains(normalizedSearch)
                || dbContext.UnitAliases.Any(
                    alias => alias.UnitId == unit.Id && alias.NormalizedAlias.Contains(normalizedSearch)));
        }

        var units = await query.ToListAsync(cancellationToken);
        var unitIds = units.Select(unit => unit.Id).ToArray();
        var aliases = await dbContext.UnitAliases
            .AsNoTracking()
            .Where(alias => unitIds.Contains(alias.UnitId))
            .OrderBy(alias => alias.Id)
            .ToListAsync(cancellationToken);
        var aliasesByUnit = aliases
            .GroupBy(alias => alias.UnitId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        return units
            .OrderBy(unit => IsPrefixMatch(unit, aliasesByUnit, normalizedSearch) ? 0 : 1)
            .ThenBy(unit => unit.Id)
            .Take(limit)
            .Select(unit => ToRecord(unit, AliasesFor(unit.Id, aliasesByUnit)))
            .ToArray();
    }

    /// <summary>
    /// Creates a custom unit. The symbol and the normalized display name must be
    /// unique; the factor rules depend on the dimension and are validated here.
    /// </summary>
    public async Task<UnitMutationResult> CreateAsync(
        string? symbol,
        string? displayName,
        UnitDimension dimension,
        decimal? baseFactor,
        bool factorRequested,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var normalizedSymbol = NormalizeRequired(symbol, "symbol", errors);
        var normalizedDisplay = NormalizeRequired(displayName, "display_name", errors);
        ValidateFactorForDimension(dimension, baseFactor, factorRequested, errors);
        if (errors.Count > 0)
        {
            return new UnitMutationResult.ValidationFailed(errors);
        }

        // Two saves (the generated id feeds the audit entity id), so they share
        // one explicit transaction: either both rows commit or neither does. The
        // transaction also holds the per-symbol advisory lock below.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await LockSymbolAsync(normalizedSymbol!.Normalized, cancellationToken);

        var conflictId = await FindConflictIdAsync(
            normalizedSymbol.Normalized,
            normalizedDisplay!.Normalized,
            excludingId: null,
            cancellationToken);
        if (conflictId is not null)
        {
            return new UnitMutationResult.Conflict(conflictId);
        }

        var unit = new Unit
        {
            Symbol = normalizedSymbol.Display,
            DisplayName = normalizedDisplay.Display,
            NormalizedName = normalizedDisplay.Normalized,
            Dimension = dimension,
            BaseFactor = RequiresFactor(dimension) ? baseFactor : null,
            IsCustom = true,
        };

        dbContext.Units.Add(unit);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost a create race on symbol or normalized_name; the advisory lock
            // above serializes equal folded symbols, so this stays a backstop
            // (for example the exact-case symbol index or a name-only race).
            dbContext.ChangeTracker.Clear();
            return new UnitMutationResult.Conflict(await FindConflictIdAsync(
                normalizedSymbol.Normalized,
                normalizedDisplay.Normalized,
                excludingId: null,
                cancellationToken));
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Created,
                AuditEntityTypes.Unit,
                EntityId: unit.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(unit, []),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created unit {UnitId}", unit.Id);
        return new UnitMutationResult.Success(ToRecord(unit, []));
    }

    /// <summary>
    /// Updates the symbol, display name, and/or base factor. The dimension is
    /// never editable here (docs/DATABASE.md §5); a factor edit that changes the
    /// value is audited as <c>factor_changed</c>, every other edit as <c>updated</c>.
    /// </summary>
    public async Task<UnitMutationResult> UpdateAsync(
        long id,
        string? symbol,
        bool symbolRequested,
        string? displayName,
        bool displayNameRequested,
        decimal? baseFactor,
        bool factorRequested,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var unit = await dbContext.Units.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (unit is null)
        {
            return new UnitMutationResult.NotFound();
        }

        var aliases = await LoadAliasesAsync(id, cancellationToken);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var normalizedSymbol = symbolRequested
            ? NormalizeRequired(symbol, "symbol", errors)
            : null;
        var normalizedDisplay = displayNameRequested
            ? NormalizeRequired(displayName, "display_name", errors)
            : null;
        if (factorRequested)
        {
            if (RequiresFactor(unit.Dimension))
            {
                if (baseFactor is null)
                {
                    errors["base_factor"] = [FactorBoundsMessage];
                }
                else if (!IsWithinFactorBounds(baseFactor.Value))
                {
                    errors["base_factor"] = [FactorBoundsMessage];
                }
            }
            else
            {
                errors["base_factor"] = [FactorNotEditableMessage];
            }
        }

        if (errors.Count > 0)
        {
            return new UnitMutationResult.ValidationFailed(errors);
        }

        // A symbol rename can race another writer; hold the per-symbol advisory
        // lock for the rest of the transaction so the folded-symbol conflict
        // check and the write are serialized.
        await using var transaction = normalizedSymbol is not null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (normalizedSymbol is not null)
        {
            await LockSymbolAsync(normalizedSymbol.Normalized, cancellationToken);
        }

        if (normalizedSymbol is not null || normalizedDisplay is not null)
        {
            var conflictId = await FindConflictIdAsync(
                normalizedSymbol?.Normalized,
                normalizedDisplay?.Normalized,
                excludingId: id,
                cancellationToken);
            if (conflictId is not null)
            {
                return new UnitMutationResult.Conflict(conflictId);
            }
        }

        var before = SnapshotJson(unit, aliases);
        if (normalizedSymbol is not null)
        {
            unit.Symbol = normalizedSymbol.Display;
        }

        if (normalizedDisplay is not null)
        {
            unit.DisplayName = normalizedDisplay.Display;
            unit.NormalizedName = normalizedDisplay.Normalized;
        }

        var factorChanged = false;
        if (factorRequested)
        {
            factorChanged = unit.BaseFactor != baseFactor!.Value;
            unit.BaseFactor = baseFactor.Value;
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                factorChanged ? AuditEventTypes.FactorChanged : AuditEventTypes.Updated,
                AuditEntityTypes.Unit,
                EntityId: unit.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(unit, aliases),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost a rename race on symbol or normalized_name.
            dbContext.ChangeTracker.Clear();
            return new UnitMutationResult.Conflict(await FindConflictIdAsync(
                normalizedSymbol?.Normalized,
                normalizedDisplay?.Normalized,
                excludingId: id,
                cancellationToken));
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Updated unit {UnitId}", id);
        return new UnitMutationResult.Success(ToRecord(unit, aliases));
    }

    /// <summary>Deactivates a unit; repeating the call is a no-op and writes no audit row.</summary>
    public async Task<UnitMutationResult> RetireAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var unit = await dbContext.Units.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (unit is null)
        {
            return new UnitMutationResult.NotFound();
        }

        var aliases = await LoadAliasesAsync(id, cancellationToken);
        if (!unit.IsActive)
        {
            return new UnitMutationResult.Success(ToRecord(unit, aliases));
        }

        var before = SnapshotJson(unit, aliases);
        unit.IsActive = false;

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Retired,
                AuditEntityTypes.Unit,
                EntityId: unit.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(unit, aliases),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Retired unit {UnitId}", id);
        return new UnitMutationResult.Success(ToRecord(unit, aliases));
    }

    /// <summary>
    /// Hard-deletes a unit only while no recipe ingredient references it
    /// The count comes from the usage seam.
    /// </summary>
    public async Task<UnitMutationResult> DeleteAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var unit = await dbContext.Units.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (unit is null)
        {
            return new UnitMutationResult.NotFound();
        }

        var usageCount = await usageChecker.CountUnitReferencesAsync(id, cancellationToken);
        if (usageCount > 0)
        {
            return new UnitMutationResult.InUse(usageCount);
        }

        var aliases = await LoadAliasesAsync(id, cancellationToken);
        var before = SnapshotJson(unit, aliases);
        dbContext.Units.Remove(unit);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Deleted,
                AuditEntityTypes.Unit,
                EntityId: unit.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted unit {UnitId}", id);
        return new UnitMutationResult.Success(ToRecord(unit, []));
    }

    /// <summary>
    /// Adds a normalized alias to a unit. Aliases are unique per unit, so the
    /// seed's <c>t</c> (tsp) and <c>T</c> (tbsp) coexist; an alias that repeats
    /// one of the unit's own alias rows, symbol, or display name conflicts.
    /// </summary>
    public async Task<UnitAliasMutationResult> AddAliasAsync(
        long unitId,
        string? alias,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var unit = await dbContext.Units.SingleOrDefaultAsync(
            candidate => candidate.Id == unitId,
            cancellationToken);
        if (unit is null)
        {
            return new UnitAliasMutationResult.NotFound();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var normalized = NormalizeRequired(alias, "alias", errors);
        if (normalized is null)
        {
            return new UnitAliasMutationResult.ValidationFailed(errors);
        }

        var aliases = await LoadAliasesAsync(unitId, cancellationToken);
        var duplicate = aliases.FirstOrDefault(
            candidate => candidate.NormalizedAlias == normalized.Normalized);
        if (duplicate is not null)
        {
            return AliasConflict(unitId, "alias", duplicate.Id);
        }

        if (string.Equals(normalized.Normalized, NormalizedSymbol(unit.Symbol), StringComparison.Ordinal))
        {
            return AliasConflict(unitId, "symbol", aliasId: null);
        }

        if (string.Equals(normalized.Normalized, unit.NormalizedName, StringComparison.Ordinal))
        {
            return AliasConflict(unitId, "display_name", aliasId: null);
        }

        var before = SnapshotJson(unit, aliases);
        var row = new UnitAlias
        {
            UnitId = unitId,
            Alias = normalized.Display,
            NormalizedAlias = normalized.Normalized,
        };

        dbContext.UnitAliases.Add(row);

        // Two saves (the generated id feeds the after snapshot and the response)
        // so they share one explicit transaction.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // Lost an alias race on (unit_id, normalized_alias).
            dbContext.ChangeTracker.Clear();
            var concurrent = await dbContext.UnitAliases
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.UnitId == unitId
                        && candidate.NormalizedAlias == normalized.Normalized,
                    cancellationToken);
            return AliasConflict(unitId, "alias", concurrent?.Id);
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Unit,
                EntityId: unit.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(unit, [.. aliases, row]),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Added alias {AliasId} to unit {UnitId}", row.Id, unitId);
        return new UnitAliasMutationResult.Success(new UnitAliasRecord(row.Id, row.Alias));
    }

    /// <summary>
    /// Removes one alias from a unit. An id that belongs to another unit (or to
    /// no unit at all) is reported as not found for this unit.
    /// </summary>
    public async Task<UnitAliasMutationResult> RemoveAliasAsync(
        long unitId,
        long aliasId,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var unit = await dbContext.Units.SingleOrDefaultAsync(
            candidate => candidate.Id == unitId,
            cancellationToken);
        if (unit is null)
        {
            return new UnitAliasMutationResult.NotFound();
        }

        var aliases = await LoadAliasesAsync(unitId, cancellationToken);
        var alias = aliases.FirstOrDefault(candidate => candidate.Id == aliasId);
        if (alias is null)
        {
            return new UnitAliasMutationResult.NotFound();
        }

        var before = SnapshotJson(unit, aliases);
        dbContext.UnitAliases.Remove(alias);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Unit,
                EntityId: unit.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(unit, aliases.Where(candidate => candidate.Id != aliasId)),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Removed alias {AliasId} from unit {UnitId}", aliasId, unitId);
        return new UnitAliasMutationResult.Success(new UnitAliasRecord(alias.Id, alias.Alias));
    }

    private static bool RequiresFactor(UnitDimension dimension) =>
        dimension is UnitDimension.Mass or UnitDimension.Volume;

    private static bool IsWithinFactorBounds(decimal value) =>
        value >= MinBaseFactor && value <= MaxBaseFactor;

    private static void ValidateFactorForDimension(
        UnitDimension dimension,
        decimal? baseFactor,
        bool factorRequested,
        Dictionary<string, string[]> errors)
    {
        if (RequiresFactor(dimension))
        {
            if (!factorRequested || baseFactor is null)
            {
                errors["base_factor"] = [FactorRequiredMessage];
            }
            else if (!IsWithinFactorBounds(baseFactor.Value))
            {
                errors["base_factor"] = [FactorBoundsMessage];
            }

            return;
        }

        if (factorRequested)
        {
            errors["base_factor"] = [FactorForbiddenMessage];
        }
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

        errors[fieldName] =
        [
            normalization is ReferenceDataNormalizationResult.Invalid
            {
                Reason: ReferenceDataNormalizationFailureReason.ControlCharacters,
            }
                ? $"{fieldName} contains unsupported control characters."
                : $"{fieldName} is required and must not be blank.",
        ];
        return null;
    }

    private static string NormalizedSymbol(string symbol)
    {
        var normalization = ReferenceDataNormalizer.Normalize(symbol);
        return normalization is ReferenceDataNormalizationResult.Success success
            ? success.Normalized
            : symbol.ToLowerInvariant();
    }

    private Task<List<UnitAlias>> LoadAliasesAsync(long unitId, CancellationToken cancellationToken) =>
        dbContext.UnitAliases
            .Where(alias => alias.UnitId == unitId)
            .OrderBy(alias => alias.Id)
            .ToListAsync(cancellationToken);

    private async Task<long?> FindConflictIdAsync(
        string? normalizedSymbol,
        string? normalizedName,
        long? excludingId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Units.AsNoTracking();
        if (excludingId is not null)
        {
            query = query.Where(unit => unit.Id != excludingId.Value);
        }

        var candidates = await query
            .OrderBy(unit => unit.Id)
            .Select(unit => new { unit.Id, unit.Symbol, unit.NormalizedName })
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (normalizedName is not null && candidate.NormalizedName == normalizedName)
            {
                return candidate.Id;
            }

            if (normalizedSymbol is not null && NormalizedSymbol(candidate.Symbol) == normalizedSymbol)
            {
                return candidate.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Takes the transaction-scoped PostgreSQL advisory lock for one folded
    /// symbol. The create and symbol-rename paths call this after their
    /// transaction begins and before the conflict check, so app-level
    /// case/diacritic uniqueness is serialized even though the unique index is
    /// exact-case. The lock is released automatically on commit or rollback.
    /// </summary>
    private Task LockSymbolAsync(string foldedSymbol, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({SymbolLockKey(foldedSymbol)})",
            cancellationToken);

    /// <summary>
    /// Stable 64-bit advisory-lock key for a folded unit symbol: the first eight
    /// bytes (big-endian) of SHA-256 over <c>"unit-symbol:" + foldedSymbol</c>.
    /// The prefix namespaces the key away from other advisory-lock users (for
    /// example the seeder's fixed key), and the fold makes every casing or
    /// diacritic variant of one symbol map to the same lock.
    /// </summary>
    private static long SymbolLockKey(string foldedSymbol)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"unit-symbol:{foldedSymbol}"));
        return BinaryPrimitives.ReadInt64BigEndian(hash);
    }

    private static bool IsPrefixMatch(
        Unit unit,
        IReadOnlyDictionary<long, UnitAlias[]> aliasesByUnit,
        string? normalizedSearch)
    {
        if (normalizedSearch is null)
        {
            return true;
        }

        if (unit.Symbol.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase)
            || unit.NormalizedName.StartsWith(normalizedSearch, StringComparison.Ordinal))
        {
            return true;
        }

        return aliasesByUnit.TryGetValue(unit.Id, out var aliases)
            && aliases.Any(alias => alias.NormalizedAlias.StartsWith(normalizedSearch, StringComparison.Ordinal));
    }

    private static UnitAlias[] AliasesFor(long unitId, IReadOnlyDictionary<long, UnitAlias[]> aliasesByUnit) =>
        aliasesByUnit.TryGetValue(unitId, out var aliases) ? aliases : [];

    private static UnitAliasMutationResult AliasConflict(long unitId, string source, long? aliasId)
    {
        var details = new Dictionary<string, object?>
        {
            ["entity_type"] = AuditEntityTypes.Unit,
            ["existing_id"] = unitId,
            ["source"] = source,
        };
        if (aliasId is not null)
        {
            details["alias_id"] = aliasId.Value;
        }

        return new UnitAliasMutationResult.AliasConflict(details);
    }

    private static string SnapshotJson(Unit unit, IEnumerable<UnitAlias> aliases) =>
        JsonSerializer.Serialize(new
        {
            id = unit.Id,
            symbol = unit.Symbol,
            display_name = unit.DisplayName,
            normalized_name = unit.NormalizedName,
            dimension = EnumTextConverters.UnitDimensionToText(unit.Dimension),
            base_factor = unit.BaseFactor,
            is_custom = unit.IsCustom,
            is_active = unit.IsActive,
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

    private static UnitRecord ToRecord(Unit unit, IReadOnlyCollection<UnitAlias> aliases) =>
        new(
            unit.Id,
            unit.Symbol,
            unit.DisplayName,
            unit.Dimension,
            unit.BaseFactor,
            unit.IsCustom,
            unit.IsActive,
            aliases
                .Where(alias => alias.UnitId == unit.Id)
                .OrderBy(alias => alias.Id)
                .Select(alias => new UnitAliasRecord(alias.Id, alias.Alias))
                .ToArray());
}
