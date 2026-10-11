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
    TimeProvider timeProvider)
{
    /// <summary>Inclusive lower bound for <c>range_days</c>.</summary>
    public const int MinRangeDays = 1;

    /// <summary>Inclusive upper bound for <c>range_days</c> (the 366-day cap).</summary>
    public const int MaxRangeDays = GroceryListService.MaxRangeDays;

    /// <summary>Creates a manual item, promoting a staple when requested.</summary>
    public async Task<GroceryManualItemOutcome> CreateAsync(
        GroceryManualItemCreateRequest request,
        long? actorUserId,
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
        var ingredient = await ResolveCanonicalIngredientAsync(request.IngredientId, errors, cancellationToken)
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

        if (request.SaveForFuture)
        {
            await PromoteStapleAsync(item, cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new GroceryManualItemOutcome.Success(ToRecord(item));
    }

    /// <summary>Applies a partial update; an explicit null clears a nullable field.</summary>
    public async Task<GroceryManualItemOutcome> UpdateAsync(
        long id,
        GroceryManualItemPatch patch,
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
            var ingredient = await ResolveCanonicalIngredientAsync(patch.IngredientId, errors, cancellationToken)
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
        if (patch.SaveForFutureProvided && patch.SaveForFuture && !wasSaveForFuture)
        {
            await PromoteStapleAsync(item, cancellationToken).ConfigureAwait(false);
        }

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new GroceryManualItemOutcome.Success(ToRecord(item));
    }

    /// <summary>
    /// Deletes the item and its household check-state row (identity
    /// <c>manual|{id}</c>) so no orphan check state remains. A staple the item may
    /// have promoted is deliberately retained.
    /// </summary>
    public async Task<GroceryManualItemDeleteOutcome> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var item = await dbContext.ManualGroceryItems
            .SingleOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return new GroceryManualItemDeleteOutcome.NotFound();
        }

        var identity = GroceryItemIdentity.ForManualItem(id);
        var checkStates = await dbContext.GroceryCheckStates
            .Where(state => state.ItemIdentityHash == identity)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (checkStates.Count > 0)
        {
            dbContext.GroceryCheckStates.RemoveRange(checkStates);
        }

        dbContext.ManualGroceryItems.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new GroceryManualItemDeleteOutcome.Deleted();
    }

    private async Task PromoteStapleAsync(ManualGroceryItem item, CancellationToken cancellationToken)
    {
        var key = StapleKey(item.Name);
        var activeNames = await dbContext.GroceryStaples
            .AsNoTracking()
            .Where(staple => staple.IsActive)
            .Select(staple => staple.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (activeNames.Any(name => string.Equals(StapleKey(name), key, StringComparison.Ordinal)))
        {
            return;
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
    }

    /// <summary>
    /// Normalized comparison key for staple de-duplication: the reference-data
    /// normalized form when the name is normalizable, else a trimmed invariant
    /// lowercase fallback so repeated promotion stays idempotent.
    /// </summary>
    private static string StapleKey(string name) =>
        ReferenceDataNormalizer.Normalize(name) is ReferenceDataNormalizationResult.Success success
            ? success.Normalized
            : name.Trim().ToLowerInvariant();

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

    private async Task<IngredientRow?> ResolveCanonicalIngredientAsync(
        long? ingredientId,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (ingredientId is not { } id)
        {
            return null;
        }

        var current = await LoadIngredientAsync(id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            errors["ingredient_id"] = [$"ingredient_id {id} does not exist."];
            return null;
        }

        // Follow the merge redirect to the canonical ingredient so aliases
        // resolve consistently with the list projection (docs/M8_PLAN.md §1.4).
        var visited = new HashSet<long>();
        while (current.MergedIntoIngredientId is { } target
            && target != current.Id
            && visited.Add(current.Id))
        {
            var next = await LoadIngredientAsync(target, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private async Task<IngredientRow?> LoadIngredientAsync(long id, CancellationToken cancellationToken) =>
        await dbContext.Ingredients
            .AsNoTracking()
            .Where(ingredient => ingredient.Id == id)
            .Select(ingredient => new IngredientRow(
                ingredient.Id,
                ingredient.DepartmentId,
                ingredient.MergedIntoIngredientId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

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

    private sealed record IngredientRow(
        long Id,
        long? DepartmentId,
        long? MergedIntoIngredientId);
}
