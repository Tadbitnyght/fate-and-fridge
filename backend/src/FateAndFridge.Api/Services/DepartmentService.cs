using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>One grocery department as exposed by the API and by audit snapshots.</summary>
public sealed record DepartmentRecord(
    long Id,
    string Name,
    int SortOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Outcome of a department create/update/retire/delete attempt.</summary>
public abstract record DepartmentMutationResult
{
    private DepartmentMutationResult()
    {
    }

    public sealed record Success(DepartmentRecord Department) : DepartmentMutationResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : DepartmentMutationResult;

    public sealed record NotFound : DepartmentMutationResult;

    public sealed record InUse(int IngredientCount) : DepartmentMutationResult;
}

/// <summary>Outcome of a department reorder attempt.</summary>
public abstract record DepartmentReorderResult
{
    private DepartmentReorderResult()
    {
    }

    public sealed record Success(IReadOnlyList<DepartmentRecord> Departments) : DepartmentReorderResult;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors) : DepartmentReorderResult;
}

/// <summary>
/// M2-05 departments management (docs/API.md §22): ordered reference data with
/// normalized-name uniqueness, retire/delete semantics, and an audit row staged
/// into the caller's <see cref="AppDbContext"/> for every write.
/// </summary>
public sealed class DepartmentService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<DepartmentService> logger)
{
    private const string DuplicateNameMessage = "A department with this name already exists.";

    private const string OrderMessage = "ids must be a complete permutation of every department id.";

    /// <summary>Lists departments ordered by sort order, prefix matches first when searching.</summary>
    public async Task<IReadOnlyList<DepartmentRecord>> ListAsync(
        string? normalizedSearch,
        int limit,
        bool includeRetired,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.GroceryDepartments.AsNoTracking();
        if (!includeRetired)
        {
            query = query.Where(department => department.IsActive);
        }

        if (normalizedSearch is not null)
        {
            query = query.Where(department => department.NormalizedName.Contains(normalizedSearch));
        }

        var departments = await query.ToListAsync(cancellationToken);
        return departments
            .OrderBy(department =>
                normalizedSearch is not null
                && !department.NormalizedName.StartsWith(normalizedSearch, StringComparison.Ordinal)
                    ? 1
                    : 0)
            .ThenBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Take(limit)
            .Select(ToRecord)
            .ToArray();
    }

    /// <summary>Creates a department and appends it to the end of the sort order.</summary>
    public async Task<DepartmentMutationResult> CreateAsync(
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

        if (await dbContext.GroceryDepartments.AnyAsync(
                department => department.NormalizedName == normalized.Normalized,
                cancellationToken))
        {
            return DuplicateName();
        }

        var now = timeProvider.GetUtcNow();
        var sortOrder = (await dbContext.GroceryDepartments.MaxAsync(
            department => (int?)department.SortOrder,
            cancellationToken) ?? 0) + 1;
        var department = new GroceryDepartment
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            SortOrder = sortOrder,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.GroceryDepartments.Add(department);

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
            return DuplicateName();
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Created,
                AuditEntityTypes.Department,
                EntityId: department.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(department),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Created department {DepartmentId}", department.Id);
        return new DepartmentMutationResult.Success(ToRecord(department));
    }

    /// <summary>Renames and/or toggles <c>is_active</c> on a department.</summary>
    public async Task<DepartmentMutationResult> UpdateAsync(
        long id,
        string? name,
        bool renameRequested,
        bool? isActive,
        bool activeRequested,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var department = await dbContext.GroceryDepartments.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (department is null)
        {
            return new DepartmentMutationResult.NotFound();
        }

        ReferenceDataNormalizationResult.Success? renamed = null;
        if (renameRequested)
        {
            var normalization = ReferenceDataNormalizer.Normalize(name);
            if (normalization is not ReferenceDataNormalizationResult.Success normalized)
            {
                return ValidationFailed("name", NameMessage(normalization));
            }

            if (!string.Equals(normalized.Normalized, department.NormalizedName, StringComparison.Ordinal)
                && await dbContext.GroceryDepartments.AnyAsync(
                    candidate => candidate.NormalizedName == normalized.Normalized && candidate.Id != id,
                    cancellationToken))
            {
                return DuplicateName();
            }

            renamed = normalized;
        }

        var before = SnapshotJson(department);
        if (renamed is not null)
        {
            department.Name = renamed.Display;
            department.NormalizedName = renamed.Normalized;
        }

        if (activeRequested)
        {
            department.IsActive = isActive!.Value;
        }

        department.UpdatedAt = timeProvider.GetUtcNow();

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Updated,
                AuditEntityTypes.Department,
                EntityId: department.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(department),
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
            return DuplicateName();
        }

        logger.LogInformation("Updated department {DepartmentId}", id);
        return new DepartmentMutationResult.Success(ToRecord(department));
    }

    /// <summary>Deactivates a department; repeating the call is a no-op and writes no audit row.</summary>
    public async Task<DepartmentMutationResult> RetireAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var department = await dbContext.GroceryDepartments.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (department is null)
        {
            return new DepartmentMutationResult.NotFound();
        }

        if (!department.IsActive)
        {
            return new DepartmentMutationResult.Success(ToRecord(department));
        }

        var before = SnapshotJson(department);
        department.IsActive = false;
        department.UpdatedAt = timeProvider.GetUtcNow();

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Retired,
                AuditEntityTypes.Department,
                EntityId: department.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                AfterJson: SnapshotJson(department),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Retired department {DepartmentId}", id);
        return new DepartmentMutationResult.Success(ToRecord(department));
    }

    /// <summary>Hard-deletes a department only while no ingredient references it.</summary>
    public async Task<DepartmentMutationResult> DeleteAsync(
        long id,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var department = await dbContext.GroceryDepartments.SingleOrDefaultAsync(
            candidate => candidate.Id == id,
            cancellationToken);
        if (department is null)
        {
            return new DepartmentMutationResult.NotFound();
        }

        var ingredientCount = await dbContext.Ingredients.CountAsync(
            ingredient => ingredient.DepartmentId == id,
            cancellationToken);
        if (ingredientCount > 0)
        {
            return new DepartmentMutationResult.InUse(ingredientCount);
        }

        var before = SnapshotJson(department);
        dbContext.GroceryDepartments.Remove(department);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Deleted,
                AuditEntityTypes.Department,
                EntityId: department.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: before,
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted department {DepartmentId}", id);
        return new DepartmentMutationResult.Success(ToRecord(department));
    }

    /// <summary>
    /// Applies a complete permutation of every department id (retired ones
    /// included) to <c>sort_order</c> in one transaction, with a reordered audit row.
    /// </summary>
    public async Task<DepartmentReorderResult> ReorderAsync(
        IReadOnlyList<long>? ids,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var departments = await dbContext.GroceryDepartments.ToListAsync(cancellationToken);
        if (ids is null
            || ids.Count != departments.Count
            || ids.Distinct().Count() != ids.Count
            || ids.Any(id => departments.All(department => department.Id != id)))
        {
            return OrderValidationFailed();
        }

        var before = departments
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => department.Id)
            .ToArray();
        var byId = departments.ToDictionary(department => department.Id);
        var now = timeProvider.GetUtcNow();
        for (var index = 0; index < ids.Count; index++)
        {
            var department = byId[ids[index]];
            department.SortOrder = index + 1;
            department.UpdatedAt = now;
        }

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.Reordered,
                AuditEntityTypes.Department,
                BeforeJson: JsonSerializer.Serialize(before),
                AfterJson: JsonSerializer.Serialize(ids),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Reordered {Count} departments", departments.Count);
        return new DepartmentReorderResult.Success(
            ids.Select(id => ToRecord(byId[id])).ToArray());
    }

    private static DepartmentMutationResult DuplicateName() =>
        ValidationFailed("name", DuplicateNameMessage);

    private static DepartmentMutationResult ValidationFailed(string field, string message) =>
        new DepartmentMutationResult.ValidationFailed(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [message],
            });

    private static DepartmentReorderResult OrderValidationFailed() =>
        new DepartmentReorderResult.ValidationFailed(
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["ids"] = [OrderMessage],
            });

    private static string NameMessage(ReferenceDataNormalizationResult normalization) =>
        normalization is ReferenceDataNormalizationResult.Invalid
        {
            Reason: ReferenceDataNormalizationFailureReason.ControlCharacters,
        }
            ? "name contains unsupported control characters."
            : "name is required and must not be blank.";

    private static string SnapshotJson(GroceryDepartment department) =>
        JsonSerializer.Serialize(new
        {
            id = department.Id,
            name = department.Name,
            normalized_name = department.NormalizedName,
            sort_order = department.SortOrder,
            is_active = department.IsActive,
        });

    private static DepartmentRecord ToRecord(GroceryDepartment department) =>
        new(
            department.Id,
            department.Name,
            department.SortOrder,
            department.IsActive,
            department.CreatedAt,
            department.UpdatedAt);
}
