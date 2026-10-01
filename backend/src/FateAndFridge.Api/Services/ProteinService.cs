using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>One protein as exposed by the API and by audit snapshots.</summary>
public sealed record ProteinRecord(
    long Id,
    string Name,
    bool IsActive);

/// <summary>Outcome of a protein create/update/retire/delete attempt.</summary>
public abstract record ProteinMutationResult
{
    private ProteinMutationResult()
    {
    }

    public sealed record Success(ProteinRecord Protein) : ProteinMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : ProteinMutationResult;

    public sealed record NotFound : ProteinMutationResult;

    public sealed record Conflict(long? ExistingId) : ProteinMutationResult;

    public sealed record InUse(int UsageCount) : ProteinMutationResult;
}

/// <summary>
/// M2-10 proteins management (docs/API.md §22, docs/DATABASE.md §7):
/// normalized-name uniqueness with <c>PROTEIN_EXISTS</c> conflicts, retire and
/// Admin-only hard-delete semantics, and an audit row staged into the caller's
/// <see cref="AppDbContext"/> for every write.
/// </summary>
public sealed class ProteinService(
    AppDbContext dbContext,
    IAuditService auditService,
    IReferenceUsageChecker usageChecker,
    ILogger<ProteinService> logger)
{
    private const string DuplicateNameMessage = "A protein with this name already exists.";

    /// <summary>Lists proteins ordered by name, prefix matches first when searching.</summary>
    public async Task<IReadOnlyList<ProteinRecord>> ListAsync(
        string? normalizedSearch,
        int limit,
        bool includeRetired,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Proteins.AsNoTracking();
        if (!includeRetired)
        {
            query = query.Where(protein => protein.IsActive);
        }

        if (normalizedSearch is not null)
        {
            query = query.Where(protein => protein.NormalizedName.Contains(normalizedSearch));
        }

        var proteins = await query.ToListAsync(cancellationToken);
        return proteins
            .OrderBy(protein =>
                normalizedSearch is not null
                && !protein.NormalizedName.StartsWith(normalizedSearch, StringComparison.Ordinal)
                    ? 1
                    : 0)
            .ThenBy(protein => protein.Name, StringComparer.Ordinal)
            .ThenBy(protein => protein.Id)
            .Take(limit)
            .Select(ToRecord)
            .ToArray();
    }

    public async Task<ProteinMutationResult> CreateAsync(
        string? name,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var normalization = ReferenceDataNormalizer.Normalize(name);
        if (normalization is not ReferenceDataNormalizationResult.Success normalized)
        {
            return ValidationFailed("name", NameMessage(normalization));
        }

        var existingId = await FindExistingIdAsync(
            normalized.Normalized,
            excludingId: null,
            cancellationToken);
        if (existingId is not null)
        {
            return new ProteinMutationResult.Conflict(existingId);
        }

        var protein = new Protein
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            IsActive = true,
        };

        dbContext.Proteins.Add(protein);

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
            return new ProteinMutationResult.Conflict(await FindExistingIdAsync(
                normalized.Normalized,
                excludingId: null,
                cancellationToken));
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Created,
                AuditEntityTypes.Protein,
                EntityId: protein.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(protein),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created protein {ProteinId}", protein.Id);
        return new ProteinMutationResult.Success(ToRecord(protein));
    }

    public async Task<ProteinMutationResult> UpdateAsync(
        long id,
        string? name,
        bool renameRequested,
        bool? isActive,
        bool activeRequested,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var protein = await dbContext.Proteins.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (protein is null)
        {
            return new ProteinMutationResult.NotFound();
        }

        ReferenceDataNormalizationResult.Success? renamed = null;
        if (renameRequested)
        {
            var normalization = ReferenceDataNormalizer.Normalize(name);
            if (normalization is not ReferenceDataNormalizationResult.Success normalized)
            {
                return ValidationFailed("name", NameMessage(normalization));
            }

            if (!string.Equals(normalized.Normalized, protein.NormalizedName, StringComparison.Ordinal))
            {
                var existingId = await FindExistingIdAsync(
                    normalized.Normalized,
                    excludingId: id,
                    cancellationToken);
                if (existingId is not null)
                {
                    return new ProteinMutationResult.Conflict(existingId);
                }
            }

            renamed = normalized;
        }

        var before = SnapshotJson(protein);
        if (renamed is not null)
        {
            protein.Name = renamed.Display;
            protein.NormalizedName = renamed.Normalized;
        }

        if (activeRequested)
        {
            protein.IsActive = isActive!.Value;
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Protein,
                EntityId: protein.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(protein),
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
            return new ProteinMutationResult.Conflict(await FindExistingIdAsync(
                renamed?.Normalized,
                excludingId: id,
                cancellationToken));
        }

        logger.LogInformation("Updated protein {ProteinId}", id);
        return new ProteinMutationResult.Success(ToRecord(protein));
    }

    public async Task<ProteinMutationResult> RetireAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var protein = await dbContext.Proteins.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (protein is null)
        {
            return new ProteinMutationResult.NotFound();
        }

        if (!protein.IsActive)
        {
            // Idempotent: a second retire is a no-op and writes no audit row.
            return new ProteinMutationResult.Success(ToRecord(protein));
        }

        var before = SnapshotJson(protein);
        protein.IsActive = false;

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Retired,
                AuditEntityTypes.Protein,
                EntityId: protein.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(protein),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Retired protein {ProteinId}", id);
        return new ProteinMutationResult.Success(ToRecord(protein));
    }

    public async Task<ProteinMutationResult> DeleteAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var protein = await dbContext.Proteins.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (protein is null)
        {
            return new ProteinMutationResult.NotFound();
        }

        var usageCount = await usageChecker.CountProteinReferencesAsync(id, cancellationToken);
        if (usageCount > 0)
        {
            return new ProteinMutationResult.InUse(usageCount);
        }

        var before = SnapshotJson(protein);
        dbContext.Proteins.Remove(protein);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Deleted,
                AuditEntityTypes.Protein,
                EntityId: protein.Id.ToString(CultureInfo.InvariantCulture),
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
            // A recipe can start referencing the protein between the count check
            // above and this delete (M3+); surface the same 409 as the pre-check
            // instead of failing the request with a 500.
            dbContext.ChangeTracker.Clear();
            var concurrentCount = await usageChecker.CountProteinReferencesAsync(id, cancellationToken);
            return new ProteinMutationResult.InUse(Math.Max(concurrentCount, 1));
        }

        logger.LogInformation("Deleted protein {ProteinId}", id);
        return new ProteinMutationResult.Success(ToRecord(protein));
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

        var query = dbContext.Proteins.AsNoTracking();
        if (excludingId is not null)
        {
            query = query.Where(protein => protein.Id != excludingId.Value);
        }

        return await query
            .Where(protein => protein.NormalizedName == normalizedName)
            .OrderBy(protein => protein.Id)
            .Select(protein => (long?)protein.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static ProteinMutationResult ValidationFailed(string field, string message) =>
        new ProteinMutationResult.ValidationFailed(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [message],
            });

    private static string NameMessage(ReferenceDataNormalizationResult normalization) =>
        normalization is ReferenceDataNormalizationResult.Invalid
        {
            Reason: ReferenceDataNormalizationFailureReason.ControlCharacters,
        }
            ? "name contains unsupported control characters."
            : "name is required and must not be blank.";

    private static string SnapshotJson(Protein protein) =>
        JsonSerializer.Serialize(new
        {
            id = protein.Id,
            name = protein.Name,
            normalized_name = protein.NormalizedName,
            is_active = protein.IsActive,
        });

    private static ProteinRecord ToRecord(Protein protein) =>
        new(
            protein.Id,
            protein.Name,
            protein.IsActive);
}
