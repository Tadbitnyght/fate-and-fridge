using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// One stored manual grocery item (docs/M8_PLAN.md §1.8, §2.3). <see cref="RangeStart"/>
/// is the inclusive household-local first date of the item's range and
/// <see cref="RangeDays"/> its length; the item is visible only while that pair
/// exactly equals the resolved list range.
/// </summary>
public sealed record GroceryManualItemRecord(
    long Id,
    string Name,
    decimal? QuantityNumeric,
    long? UnitId,
    long? IngredientId,
    long? DepartmentId,
    DateOnly RangeStart,
    int RangeDays,
    bool SaveForFuture,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Outcome of a manual grocery item create or update.</summary>
public abstract record GroceryManualItemOutcome
{
    private GroceryManualItemOutcome()
    {
    }

    /// <summary>The created or updated item.</summary>
    public sealed record Success(GroceryManualItemRecord Item) : GroceryManualItemOutcome;

    /// <summary>The request failed validation (range or unknown reference data).</summary>
    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
        : GroceryManualItemOutcome;

    /// <summary>The item does not exist.</summary>
    public sealed record NotFound : GroceryManualItemOutcome;
}

/// <summary>Outcome of a manual grocery item delete.</summary>
public abstract record GroceryManualItemDeleteOutcome
{
    private GroceryManualItemDeleteOutcome()
    {
    }

    /// <summary>The item and its check-state row were removed.</summary>
    public sealed record Deleted : GroceryManualItemDeleteOutcome;

    /// <summary>The item does not exist.</summary>
    public sealed record NotFound : GroceryManualItemDeleteOutcome;
}

/// <summary>
/// Range-scoped manual grocery CRUD (docs/M8_PLAN.md §1.8, §2.2). The service
/// validates referenced units/ingredients/departments, canonicalizes an
/// ingredient through its merge redirect, applies the department default, and
/// promotes a household staple for <c>save_for_future</c> idempotently. Manual
/// lines are never merged into recipe buckets and are hidden the moment the
/// active range changes (the read side owns that filter).
/// </summary>
public sealed class GroceryManualItemService(
    AppDbContext dbContext,
    HouseholdClock clock,
    TimeProvider timeProvider,
    IAuditService auditService)
{
    /// <summary>Inclusive lower bound for <c>range_days</c>.</summary>
    public const int MinRangeDays = 1;

    /// <summary>Inclusive upper bound for <c>range_days</c> (the 366-day cap).</summary>
    public const int MaxRangeDays = GroceryListService.MaxRangeDays;

    /// <summary>
    /// Creates a manual item, promoting a staple when requested. The item, the
    /// optional staple promotion, and one <c>groceries.manual_item_added</c>
    /// audit row commit atomically.
    /// </summary>
    public async Task<GroceryManualItemOutcome> CreateAsync(
        GroceryManualItemCreateRequest request,
        long? actorUserId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.RangeStart < context.Today)
        {
            errors["range_start"] = ["range_start must be today or later."];
        }

        await ValidateUnitAsync(request.UnitId, errors, cancellationToken).ConfigureAwait(false);
        await ValidateDepartmentAsync(request.DepartmentId, errors, cancellationToken).ConfigureAwait(false);
        var ingredient = await GroceryIngredientResolver
            .ResolveCanonicalAsync(dbContext, request.IngredientId, errors, cancellationToken)
            .ConfigureAwait(false);

        if (errors.Count > 0)
        {
            return new GroceryManualItemOutcome.ValidationFailed(errors);
        }

        // Department default: explicit department_id, else the linked canonical
        // ingredient's department, else null ("Other", docs/M8_PLAN.md §1.8/§1.12).
        var departmentId = request.DepartmentId ?? ingredient?.DepartmentId;

        var now = timeProvider.GetUtcNow();
        var item = new ManualGroceryItem
        {
            Name = request.Name,
            QuantityNumeric = request.QuantityNumeric,
            UnitId = request.UnitId,
            IngredientId = ingredient?.Id,
            DepartmentId = departmentId,
            RangeStart = request.RangeStart,
            RangeDays = request.RangeDays,
            SaveForFuture = request.SaveForFuture,
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.ManualGroceryItems.Add(item);

        var staplePromoted = false;
        if (request.SaveForFuture)
        {
            staplePromoted = await PromoteStapleAsync(item, cancellationToken).ConfigureAwait(false);
        }

        // Item, promotion, and audit commit atomically (docs/M12_PLAN.md §2.1).
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.GroceriesManualItemAdded,
                AuditEntityTypes.GroceryManualItem,
                EntityId: item.Id.ToString(CultureInfo.InvariantCulture),
                AfterJson: SnapshotJson(item),
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: new Dictionary<string, object?> { ["staple_promoted"] = staplePromoted }),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);

        return new GroceryManualItemOutcome.Success(ToRecord(item));
    }

    /// <summary>
    /// Applies a partial update; an explicit null clears a nullable field. The
    /// item change and its <c>groceries.manual_item_updated</c> audit row commit
    /// atomically.
    /// </summary>
    public async Task<GroceryManualItemOutcome> UpdateAsync(
        long id,
        GroceryManualItemPatch patch,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var item = await dbContext.ManualGroceryItems
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return new GroceryManualItemOutcome.NotFound();
        }

        var beforeJson = SnapshotJson(item);
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var wasSaveForFuture = item.SaveForFuture;

        if (patch.NameProvided)
        {
            item.Name = patch.Name!;
        }

        if (patch.QuantityProvided)
        {
            item.QuantityNumeric = patch.QuantityNumeric;
        }

        if (patch.UnitProvided)
        {
            await ValidateUnitAsync(patch.UnitId, errors, cancellationToken).ConfigureAwait(false);
            item.UnitId = patch.UnitId;
        }

        if (patch.IngredientProvided)
        {
            var ingredient = await GroceryIngredientResolver
                .ResolveCanonicalAsync(dbContext, patch.IngredientId, errors, cancellationToken)
                .ConfigureAwait(false);
            item.IngredientId = ingredient?.Id;
        }

        if (patch.DepartmentProvided)
        {
            await ValidateDepartmentAsync(patch.DepartmentId, errors, cancellationToken).ConfigureAwait(false);
            item.DepartmentId = patch.DepartmentId;
        }

        if (patch.RangeStartProvided && patch.RangeStart is { } rangeStart)
        {
            if (rangeStart < context.Today)
            {
                errors["range_start"] = ["range_start must be today or later."];
            }
            else
            {
                item.RangeStart = rangeStart;
            }
        }

        if (patch.RangeDaysProvided && patch.RangeDays is { } rangeDays)
        {
            item.RangeDays = rangeDays;
        }

        if (patch.SaveForFutureProvided)
        {
            item.SaveForFuture = patch.SaveForFuture;
        }

        if (errors.Count > 0)
        {
            return new GroceryManualItemOutcome.ValidationFailed(errors);
        }

        item.UpdatedAt = timeProvider.GetUtcNow();

        // Transitioning save_for_future to true promotes the staple; the
        // promotion is idempotent by normalized name (docs/M8_PLAN.md §1.8/§1.9).
        var staplePromoted = false;
        if (patch.SaveForFutureProvided && patch.SaveForFuture && !wasSaveForFuture)
        {
            staplePromoted = await PromoteStapleAsync(item, cancellationToken).ConfigureAwait(false);
        }

        // Item change, promotion, and audit commit atomically.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.GroceriesManualItemUpdated,
                AuditEntityTypes.GroceryManualItem,
                EntityId: item.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: SnapshotJson(item),
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: new Dictionary<string, object?> { ["staple_promoted"] = staplePromoted }),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);

        return new GroceryManualItemOutcome.Success(ToRecord(item));
    }

    /// <summary>
    /// Deletes the item and its household check-state row (identity
    /// <c>manual|{id}</c>) so no orphan check state remains. A staple the item may
    /// have promoted is deliberately retained. The removal and its
    /// <c>groceries.manual_item_removed</c> audit row commit atomically.
    /// </summary>
    public async Task<GroceryManualItemDeleteOutcome> DeleteAsync(
        long id,
        long? actorUserId = null,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var item = await dbContext.ManualGroceryItems
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return new GroceryManualItemDeleteOutcome.NotFound();
        }

        var beforeJson = SnapshotJson(item);
        var identity = GroceryItemIdentity.ForManualItem(id);
        var checkStates = await dbContext.GroceryCheckStates
            .Where(state => state.ItemIdentityHash == identity)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (checkStates.Count > 0)
        {
            dbContext.GroceryCheckStates.RemoveRange(checkStates);
        }

        dbContext.ManualGroceryItems.Remove(item);
        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.GroceriesManualItemRemoved,
                AuditEntityTypes.GroceryManualItem,
                EntityId: id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                IpAddress: ipAddress,
                ActorUserId: actorUserId),
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken);
        return new GroceryManualItemDeleteOutcome.Deleted();
    }

    /// <summary>Promotes a staple when no active staple shares the name.</summary>
    private async Task<bool> PromoteStapleAsync(ManualGroceryItem item, CancellationToken cancellationToken)
    {
        var key = GroceryStapleName.Key(item.Name);
        var activeNames = await dbContext.GroceryStaples
            .AsNoTracking()
            .Where(staple => staple.IsActive)
            .Select(staple => staple.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeNames.Any(name => string.Equals(GroceryStapleName.Key(name), key, StringComparison.Ordinal)))
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        dbContext.GroceryStaples.Add(new GroceryStaple
        {
            Name = item.Name,
            IngredientId = item.IngredientId,
            DepartmentId = item.DepartmentId,
            DefaultQuantityNumeric = item.QuantityNumeric,
            UnitId = item.UnitId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        });
        return true;
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

    /// <summary>Secret-free manual item snapshot for audit before/after.</summary>
    private static string SnapshotJson(ManualGroceryItem item) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = item.Id,
            ["name"] = item.Name,
            ["quantity_numeric"] = item.QuantityNumeric,
            ["unit_id"] = item.UnitId,
            ["ingredient_id"] = item.IngredientId,
            ["department_id"] = item.DepartmentId,
            ["range_start"] = item.RangeStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["range_days"] = item.RangeDays,
            ["save_for_future"] = item.SaveForFuture,
        });

    private static GroceryManualItemRecord ToRecord(ManualGroceryItem item) =>
        new(
            item.Id,
            item.Name,
            item.QuantityNumeric,
            item.UnitId,
            item.IngredientId,
            item.DepartmentId,
            item.RangeStart,
            item.RangeDays,
            item.SaveForFuture,
            item.CreatedAt,
            item.UpdatedAt);
}
