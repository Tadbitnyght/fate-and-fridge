namespace FateAndFridge.Api.Domain;

/// <summary>
/// A range-scoped free-text or ingredient-linked grocery item added by hand
/// The item belongs to the range that begins at
/// <see cref="RangeStart"/> and spans <see cref="RangeDays"/> days, and is
/// hidden when the active range changes. <see cref="SaveForFuture"/> promotes
/// it to a household staple. Department is optional (M8_PLAN.md §1.12 sends
/// department-less items to "Other last"). Timestamps are UTC.
/// </summary>
public sealed class ManualGroceryItem
{
    public long Id { get; set; }

    public required string Name { get; set; }

    public long? IngredientId { get; set; }

    /// <summary>Optional; ("Other last").</summary>
    public long? DepartmentId { get; set; }

    public decimal? QuantityNumeric { get; set; }

    public long? UnitId { get; set; }

    /// <summary>Inclusive household-local first date of the active range.</summary>
    public DateOnly RangeStart { get; set; }

    /// <summary>Number of days in the active range.</summary>
    public int RangeDays { get; set; }

    /// <summary>When true the item also promotes a household staple on save.</summary>
    public bool SaveForFuture { get; set; }

    /// <summary>Creator, or null for the system/single-user actor.</summary>
    public long? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
