using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// M3 implementation of the reference-data usage seam (docs/M3_PLAN.md §2,
/// M3-06/M3-08): counts recipe references so ingredient, unit, protein, and tag
/// hard deletes stay blocked while any recipe — retired recipes included —
/// still references the row, and reassigns recipe ingredient references during
/// an ingredient merge.
/// </summary>
public sealed class ReferenceDataUsageChecker(AppDbContext dbContext) : IReferenceUsageChecker
{
    /// <inheritdoc />
    public Task<int> CountUnitReferencesAsync(
        long unitId,
        CancellationToken cancellationToken = default) =>
        dbContext.RecipeIngredients
            .AsNoTracking()
            .CountAsync(ingredient => ingredient.UnitId == unitId, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountIngredientReferencesAsync(
        long ingredientId,
        CancellationToken cancellationToken = default) =>
        dbContext.RecipeIngredients
            .AsNoTracking()
            .CountAsync(ingredient => ingredient.IngredientId == ingredientId, cancellationToken);

    /// <inheritdoc />
    public Task<int> ReassignIngredientReferencesAsync(
        long sourceId,
        long targetId,
        CancellationToken cancellationToken = default) =>
        dbContext.RecipeIngredients
            .Where(ingredient => ingredient.IngredientId == sourceId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(ingredient => ingredient.IngredientId, targetId),
                cancellationToken);

    /// <inheritdoc />
    public Task<int> CountProteinReferencesAsync(
        long proteinId,
        CancellationToken cancellationToken = default) =>
        dbContext.RecipeProteins
            .AsNoTracking()
            .CountAsync(link => link.ProteinId == proteinId, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountTagReferencesAsync(
        long tagId,
        CancellationToken cancellationToken = default) =>
        dbContext.RecipeTags
            .AsNoTracking()
            .CountAsync(link => link.TagId == tagId, cancellationToken);
}
