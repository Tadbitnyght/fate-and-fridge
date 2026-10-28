namespace FateAndFridge.Api.Domain;

/// <summary>
/// Household-wide reusable grocery staple. A
/// staple is never added to the active list automatically; the user explicitly
/// adds it to the current range, which creates a manual item. The optional
/// <see cref="IngredientId"/>, <see cref="DepartmentId"/>, and
/// <see cref="UnitId"/> let a staple be free text or reference canonical
/// reference data. Timestamps are set by the application and are always UTC.
/// </summary>
public sealed class GroceryStaple
{
    public long Id { get; set; }

    public required string Name { get; set; }

    public long? IngredientId { get; set; }

    /// <summary>
    /// Optional department. docs/DATABASE.md §9 lists <c>department_id</c> as
    /// non-null, but M8_PLAN.md §2.3 marks it optional and decision §1.12 sends
    /// department-less items to "Other last"; M8_PLAN wins here.
    /// </summary>
    public long? DepartmentId { get; set; }

    public decimal? DefaultQuantityNumeric { get; set; }

    public long? UnitId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
