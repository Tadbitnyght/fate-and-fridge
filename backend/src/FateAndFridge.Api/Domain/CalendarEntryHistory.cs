namespace FateAndFridge.Api.Domain;

/// <summary>
/// Before/after snapshot of a calendar change (docs/DATABASE.md §8). Used for
/// temporary undo (M6) and historical corrections (M12); it is not the admin
/// audit log. Rows survive hard deletion of the entry (<c>CalendarEntryId</c>
/// becomes null) because <see cref="MealDate"/> is copied.
/// </summary>
public sealed class CalendarEntryHistory
{
    public long Id { get; set; }

    public long? CalendarEntryId { get; set; }

    public DateOnly MealDate { get; set; }

    /// <summary>Groups the rows of one atomic operation for undo (M6).</summary>
    public Guid? OperationId { get; set; }

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public long? ChangedByUserId { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public string? ChangeReason { get; set; }

    public bool IsHistoricalCorrection { get; set; }
}
