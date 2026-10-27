namespace FateAndFridge.Api.Domain;

/// <summary>
/// Measurement family for a unit (docs/DATABASE.md §5). Stored as snake_case
/// text; the dimension is immutable after creation.
/// </summary>
public enum UnitDimension
{
    Mass,
    Volume,
    Count,
    Nonconvertible,
}
