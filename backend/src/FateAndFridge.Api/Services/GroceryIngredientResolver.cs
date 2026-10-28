using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Shared, loop-safe canonical-ingredient resolution for the grocery services
/// A merged source ingredient follows its
/// <c>merged_into_ingredient_id</c> redirect to the canonical ingredient so
/// writes and the grocery list projection agree on identity; a visited set
/// makes a cyclic or self redirect terminate.
/// </summary>
internal static class GroceryIngredientResolver
{
    /// <summary>The minimal ingredient projection needed for canonicalization.</summary>
    internal sealed record IngredientRow(long Id, long? DepartmentId, long? MergedIntoIngredientId);

    /// <summary>
    /// Resolves a nullable ingredient id to its canonical row. An unknown id
    /// records a <c>validation_failed</c> message on
    /// <paramref name="errors"/> keyed <c>ingredient_id</c> and returns null.
    /// </summary>
    public static async Task<IngredientRow?> ResolveCanonicalAsync(
        AppDbContext dbContext,
        long? ingredientId,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (ingredientId is not { } id)
        {
            return null;
        }

        var current = await LoadAsync(dbContext, id, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            errors["ingredient_id"] = [$"ingredient_id {id} does not exist."];
            return null;
        }

        var visited = new HashSet<long>();
        while (current.MergedIntoIngredientId is { } target
            && target != current.Id
            && visited.Add(current.Id))
        {
            var next = await LoadAsync(dbContext, target, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private static async Task<IngredientRow?> LoadAsync(
        AppDbContext dbContext,
        long id,
        CancellationToken cancellationToken) =>
        await dbContext.Ingredients
            .AsNoTracking()
            .Where(ingredient => ingredient.Id == id)
            .Select(ingredient => new IngredientRow(
                ingredient.Id,
                ingredient.DepartmentId,
                ingredient.MergedIntoIngredientId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
}
