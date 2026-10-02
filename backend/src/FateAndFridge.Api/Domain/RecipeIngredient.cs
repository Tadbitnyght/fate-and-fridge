namespace FateAndFridge.Api.Domain;

/// <summary>
/// One ingredient line in a recipe (docs/DATABASE.md §6). Quantities and units
/// are optional; quantity-less values such as "to taste" remain valid.
/// </summary>
public sealed class RecipeIngredient
{
    public long Id { get; set; }

    public long RecipeId { get; set; }

    public long? GroupId { get; set; }

    public long IngredientId { get; set; }

    public decimal? QuantityNumeric { get; set; }

    public long? UnitId { get; set; }

    public string? PreparationNote { get; set; }

    public bool IsOptional { get; set; }

    public int SortOrder { get; set; }
}
