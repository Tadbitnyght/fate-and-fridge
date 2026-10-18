using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// One stored household staple (docs/M8_PLAN.md §1.9, §2.3). Timestamps are UTC.
/// </summary>
public sealed record GroceryStapleRecord(
    long Id,
    string Name,
    long? IngredientId,
    long? DepartmentId,
    decimal? DefaultQuantityNumeric,
    long? UnitId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Outcome of a staple create or update.</summary>
public abstract record GroceryStapleOutcome
{
    private GroceryStapleOutcome()
    {
    }

    /// <summary>The created or updated staple.</summary>
    public sealed record Success(GroceryStapleRecord Staple) : GroceryStapleOutcome;

    /// <summary>The request failed validation (unknown reference data).</summary>
    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
        : GroceryStapleOutcome;

    /// <summary>An active staple with the same normalized name already exists.</summary>
    public sealed record StapleExists(long ExistingId) : GroceryStapleOutcome;

    /// <summary>The staple does not exist.</summary>
    public sealed record NotFound : GroceryStapleOutcome;
}

/// <summary>Outcome of adding a staple to the active range as a manual item.</summary>
public abstract record GroceryStapleAddOutcome
{
    private GroceryStapleAddOutcome()
    {
    }

    /// <summary>The created manual item.</summary>
    public sealed record Success(GroceryManualItemRecord Item) : GroceryStapleAddOutcome;

    /// <summary>The add-to-list request failed validation (range/reference data).</summary>
    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
        : GroceryStapleAddOutcome;

    /// <summary>The staple does not exist or is inactive.</summary>
    public sealed record NotFound : GroceryStapleAddOutcome;
}

/// <summary>
/// Household-wide reusable staple catalog (docs/M8_PLAN.md §1.9, §2.1, §2.2): CRUD
/// plus add-to-list. Staples are <b>never auto-added</b> to a grocery list; the
/// user explicitly adds one, which creates a range-scoped manual item through
/// <see cref="GroceryManualItemService"/> so range validation, canonicalization,
/// the department default, and the response shape stay identical. Adding never
/// mutates the staple.
/// </summary>
public sealed class GroceryStapleService(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    GroceryManualItemService manualItems,
    IAuditService auditService)
{
    /// <summary>
    /// Lists the catalog ordered by name (case-insensitive, then ordinal), then id.
    /// </summary>
    public async Task<IReadOnlyList<GroceryStapleRecord>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.GroceryStaples
            .AsNoTracking()
            .Where(staple => includeInactive || staple.IsActive)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .OrderBy(staple => staple.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(staple => staple.Name, StringComparer.Ordinal)
            .ThenBy(staple => staple.Id)
            .Select(ToRecord)
            .ToList();
    }

    /// <summary>Creates a staple; an active duplicate name is rejected.</summary>
    public async Task<GroceryStapleOutcome> CreateAsync(
        GroceryStapleCreateRequest request,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await ValidateUnitAsync(request.UnitId, errors, cancellationToken).ConfigureAwait(false);
        await ValidateDepartmentAsync(request.DepartmentId, errors, cancellationToken).ConfigureAwait(false);
        var ingredient = await GroceryIngredientResolver
            .ResolveCanonicalAsync(dbContext, request.IngredientId, errors, cancellationToken)
            .ConfigureAwait(false);

        if (errors.Count > 0)
        {
            return new GroceryStapleOutcome.ValidationFailed(errors);
        }

        if (await FindActiveIdByKeyAsync(GroceryStapleName.Key(request.Name), excludeId: null, cancellationToken)
            .ConfigureAwait(false) is { } existingId)
        {
            return new GroceryStapleOutcome.StapleExists(existingId);
        }

        // Department default: explicit department_id, else the linked canonical
        // ingredient's department, else null (docs/M8_PLAN.md §1.9/§1.12).
        var departmentId = request.DepartmentId ?? ingredient?.DepartmentId;

        var now = timeProvider.GetUtcNow();
        var staple = new GroceryStaple
        {
            Name = request.Name,
            IngredientId = ingredient?.Id,
            DepartmentId = departmentId,
            DefaultQuantityNumeric = request.DefaultQuantityNumeric,
            UnitId = request.UnitId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.GroceryStaples.Add(staple);

        // Staple and its audit row commit atomically (docs/M12_PLAN.md §2.1).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.GroceriesStapleCreated,
                AuditEntityTypes.GroceryStaple,
                EntityId: staple.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(staple),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return new GroceryStapleOutcome.Success(ToRecord(staple));
    }

    /// <summary>
    /// Applies a partial update; an explicit null clears a nullable field. A name
    /// change (or reactivation) that would collide with another active staple is
    /// rejected. Deactivating an active staple is audited as
    /// <c>groceries.staple_retired</c>, every other edit as
    /// <c>groceries.staple_updated</c> (exactly one row per write).
    /// </summary>
    public async Task<GroceryStapleOutcome> UpdateAsync(
        long id,
        GroceryStaplePatch patch,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var staple = await dbContext.GroceryStaples
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (staple is null)
        {
            return new GroceryStapleOutcome.NotFound();
        }

        var beforeJson = SnapshotJson(staple);
        var wasActive = staple.IsActive;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (patch.UnitProvided)
        {
            await ValidateUnitAsync(patch.UnitId, errors, cancellationToken).ConfigureAwait(false);
        }

        if (patch.DepartmentProvided)
        {
            await ValidateDepartmentAsync(patch.DepartmentId, errors, cancellationToken).ConfigureAwait(false);
        }

        GroceryIngredientResolver.IngredientRow? ingredient = null;
        if (patch.IngredientProvided)
        {
            ingredient = await GroceryIngredientResolver
                .ResolveCanonicalAsync(dbContext, patch.IngredientId, errors, cancellationToken)
                .ConfigureAwait(false);
        }

        if (errors.Count > 0)
        {
            return new GroceryStapleOutcome.ValidationFailed(errors);
        }

        var effectiveName = patch.NameProvided ? patch.Name! : staple.Name;
        var effectiveActive = patch.IsActiveProvided ? patch.IsActive : staple.IsActive;

        if (effectiveActive)
        {
            var conflictId = await FindActiveIdByKeyAsync(
                GroceryStapleName.Key(effectiveName),
                staple.Id,
                cancellationToken).ConfigureAwait(false);
            if (conflictId is { } existingId)
            {
                return new GroceryStapleOutcome.StapleExists(existingId);
            }
        }

        if (patch.NameProvided)
        {
            staple.Name = patch.Name!;
        }

        if (patch.IngredientProvided)
        {
            staple.IngredientId = ingredient?.Id;
        }

        if (patch.DepartmentProvided)
        {
            staple.DepartmentId = patch.DepartmentId;
        }

        if (patch.QuantityProvided)
        {
            staple.DefaultQuantityNumeric = patch.DefaultQuantityNumeric;
        }

        if (patch.UnitProvided)
        {
            staple.UnitId = patch.UnitId;
        }

        if (patch.IsActiveProvided)
        {
            staple.IsActive = patch.IsActive;
        }

        staple.UpdatedAt = timeProvider.GetUtcNow();

        // Staple and its audit row commit atomically (docs/M12_PLAN.md §2.1).
        var retired = wasActive && !staple.IsActive;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditService.WriteAsync(
            new AuditEventEntry(
                retired ? AuditEventTypes.GroceriesStapleRetired : AuditEventTypes.GroceriesStapleUpdated,
                AuditEntityTypes.GroceryStaple,
                EntityId: staple.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: SnapshotJson(staple),
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return new GroceryStapleOutcome.Success(ToRecord(staple));
    }

    /// <summary>
    /// Adds an <b>active</b> staple to the active range as a manual grocery item
    /// (docs/M8_PLAN.md §1.9). A missing or inactive staple is not found. The
    /// staple row is never modified.
    /// </summary>
    public async Task<GroceryStapleAddOutcome> AddToListAsync(
        long id,
        GroceryStapleAddToListRequest range,
        long? actorUserId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);

        var staple = await dbContext.GroceryStaples
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == id && candidate.IsActive, cancellationToken)
            .ConfigureAwait(false);
        if (staple is null)
        {
            return new GroceryStapleAddOutcome.NotFound();
        }

        // The manual create path owns range validation, ingredient
        // canonicalization, the department default, and the response shape.
        var request = new GroceryManualItemCreateRequest(
            staple.Name,
            staple.DefaultQuantityNumeric,
            staple.UnitId,
            staple.IngredientId,
            staple.DepartmentId,
            range.RangeStart,
            range.RangeDays,
            SaveForFuture: false);

        var outcome = await manualItems
            .CreateAsync(request, actorUserId, ipAddress, cancellationToken)
            .ConfigureAwait(false);
        return outcome switch
        {
            GroceryManualItemOutcome.Success success =>
                new GroceryStapleAddOutcome.Success(success.Item),
            GroceryManualItemOutcome.ValidationFailed failed =>
                new GroceryStapleAddOutcome.ValidationFailed(failed.FieldErrors),
            _ => new GroceryStapleAddOutcome.NotFound(),
        };
    }

    private async Task<long?> FindActiveIdByKeyAsync(
        string key,
        long? excludeId,
        CancellationToken cancellationToken)
    {
        var active = await dbContext.GroceryStaples
            .AsNoTracking()
            .Where(staple => staple.IsActive)
            .Select(staple => new { staple.Id, staple.Name })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in active)
        {
            if ((excludeId is null || row.Id != excludeId)
                && string.Equals(GroceryStapleName.Key(row.Name), key, StringComparison.Ordinal))
            {
                return row.Id;
            }
        }

        return null;
    }

    private async Task ValidateUnitAsync(
        long? unitId,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (unitId is not { } id)
        {
            return;
        }

        var exists = await dbContext.Units
            .AsNoTracking()
            .AnyAsync(unit => unit.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            errors["unit_id"] = [$"unit_id {id} does not exist."];
        }
    }

    private async Task ValidateDepartmentAsync(
        long? departmentId,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (departmentId is not { } id)
        {
            return;
        }

        var exists = await dbContext.GroceryDepartments
            .AsNoTracking()
            .AnyAsync(department => department.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (!exists)
        {
            errors["department_id"] = [$"department_id {id} does not exist."];
        }
    }

    /// <summary>Secret-free staple snapshot for audit before/after.</summary>
    private static string SnapshotJson(GroceryStaple staple) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = staple.Id,
            ["name"] = staple.Name,
            ["ingredient_id"] = staple.IngredientId,
            ["department_id"] = staple.DepartmentId,
            ["default_quantity_numeric"] = staple.DefaultQuantityNumeric,
            ["unit_id"] = staple.UnitId,
            ["is_active"] = staple.IsActive,
        });

    private static GroceryStapleRecord ToRecord(GroceryStaple staple) =>
        new(
            staple.Id,
            staple.Name,
            staple.IngredientId,
            staple.DepartmentId,
            staple.DefaultQuantityNumeric,
            staple.UnitId,
            staple.IsActive,
            staple.CreatedAt,
            staple.UpdatedAt);
}
