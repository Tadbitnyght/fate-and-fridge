using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>One tag as exposed by the API and by audit snapshots.</summary>
public sealed record TagRecord(
    long Id,
    string Name);

/// <summary>Outcome of a tag create/update/delete attempt.</summary>
public abstract record TagMutationResult
{
    private TagMutationResult()
    {
    }

    public sealed record Success(TagRecord Tag) : TagMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : TagMutationResult;

    public sealed record NotFound : TagMutationResult;

    public sealed record Conflict(long? ExistingId) : TagMutationResult;

    public sealed record InUse(int UsageCount) : TagMutationResult;
}

/// <summary>
/// Tags management (docs/API.md §22, docs/DATABASE.md §7): normalized-name
/// uniqueness with <c>TAG_EXISTS</c> conflicts, no retire concept, Admin-only
/// hard delete, and an audit row staged into the caller's
/// <see cref="AppDbContext"/> for every write.
/// </summary>
public sealed class TagService(
    AppDbContext dbContext,
    IAuditService auditService,
    IReferenceUsageChecker usageChecker,
    ILogger<TagService> logger)
{
    private const string DuplicateNameMessage = "A tag with this name already exists.";

    /// <summary>
    /// Lists tags ordered by name, prefix matches first when searching.
    /// <paramref name="includeRetired"/> is accepted for contract uniformity but
    /// tags have no retired concept, so it has no effect.
    /// </summary>
    public async Task<IReadOnlyList<TagRecord>> ListAsync(
        string? normalizedSearch,
        int limit,
        bool includeRetired,
        CancellationToken cancellationToken = default)
    {
        _ = includeRetired;
        var query = dbContext.Tags.AsNoTracking();

        if (normalizedSearch is not null)
        {
            query = query.Where(tag => tag.NormalizedName.Contains(normalizedSearch));
        }

        var tags = await query.ToListAsync(cancellationToken);
        return tags
            .OrderBy(tag =>
                normalizedSearch is not null
                && !tag.NormalizedName.StartsWith(normalizedSearch, StringComparison.Ordinal)
                    ? 1
                    : 0)
            .ThenBy(tag => tag.Name, StringComparer.Ordinal)
            .ThenBy(tag => tag.Id)
            .Take(limit)
            .Select(ToRecord)
            .ToArray();
    }

    public async Task<TagMutationResult> CreateAsync(
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
            return new TagMutationResult.Conflict(existingId);
        }

        var tag = new Tag
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
        };

        dbContext.Tags.Add(tag);

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
            return new TagMutationResult.Conflict(await FindExistingIdAsync(
                normalized.Normalized,
                excludingId: null,
                cancellationToken));
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Created,
                AuditEntityTypes.Tag,
                EntityId: tag.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(tag),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created tag {TagId}", tag.Id);
        return new TagMutationResult.Success(ToRecord(tag));
    }

    public async Task<TagMutationResult> UpdateAsync(
        long id,
        string? name,
        bool renameRequested,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var tag = await dbContext.Tags.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (tag is null)
        {
            return new TagMutationResult.NotFound();
        }

        ReferenceDataNormalizationResult.Success? renamed = null;
        if (renameRequested)
        {
            var normalization = ReferenceDataNormalizer.Normalize(name);
            if (normalization is not ReferenceDataNormalizationResult.Success normalized)
            {
                return ValidationFailed("name", NameMessage(normalization));
            }

            if (!string.Equals(normalized.Normalized, tag.NormalizedName, StringComparison.Ordinal))
            {
                var existingId = await FindExistingIdAsync(
                    normalized.Normalized,
                    excludingId: id,
                    cancellationToken);
                if (existingId is not null)
                {
                    return new TagMutationResult.Conflict(existingId);
                }
            }

            renamed = normalized;
        }

        var before = SnapshotJson(tag);
        if (renamed is not null)
        {
            tag.Name = renamed.Display;
            tag.NormalizedName = renamed.Normalized;
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Tag,
                EntityId: tag.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(tag),
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
            return new TagMutationResult.Conflict(await FindExistingIdAsync(
                renamed?.Normalized,
                excludingId: id,
                cancellationToken));
        }

        logger.LogInformation("Updated tag {TagId}", id);
        return new TagMutationResult.Success(ToRecord(tag));
    }

    public async Task<TagMutationResult> DeleteAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var tag = await dbContext.Tags.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (tag is null)
        {
            return new TagMutationResult.NotFound();
        }

        var usageCount = await usageChecker.CountTagReferencesAsync(id, cancellationToken);
        if (usageCount > 0)
        {
            return new TagMutationResult.InUse(usageCount);
        }

        var before = SnapshotJson(tag);
        dbContext.Tags.Remove(tag);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Deleted,
                AuditEntityTypes.Tag,
                EntityId: tag.Id.ToString(CultureInfo.InvariantCulture),
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
            // A recipe can start referencing the tag between the count check
            // above and this delete (M3+); surface the same 409 as the pre-check
            // instead of failing the request with a 500.
            dbContext.ChangeTracker.Clear();
            var concurrentCount = await usageChecker.CountTagReferencesAsync(id, cancellationToken);
            return new TagMutationResult.InUse(Math.Max(concurrentCount, 1));
        }

        logger.LogInformation("Deleted tag {TagId}", id);
        return new TagMutationResult.Success(ToRecord(tag));
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

        var query = dbContext.Tags.AsNoTracking();
        if (excludingId is not null)
        {
            query = query.Where(tag => tag.Id != excludingId.Value);
        }

        return await query
            .Where(tag => tag.NormalizedName == normalizedName)
            .OrderBy(tag => tag.Id)
            .Select(tag => (long?)tag.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static TagMutationResult ValidationFailed(string field, string message) =>
        new TagMutationResult.ValidationFailed(
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

    private static string SnapshotJson(Tag tag) =>
        JsonSerializer.Serialize(new
        {
            id = tag.Id,
            name = tag.Name,
            normalized_name = tag.NormalizedName,
        });

    private static TagRecord ToRecord(Tag tag) =>
        new(
            tag.Id,
            tag.Name);
}
