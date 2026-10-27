namespace FateAndFridge.Api.Services;

/// <summary>
/// Recipe-usage seam for the reference-data hard deletes (docs/M2_PLAN.md §1.11).
/// Counts how many recipes reference an entity; M2-06's unit delete consumes
/// <see cref="CountUnitReferencesAsync"/> and M2-08/M2-10 consume the rest.
/// </summary>
/// <remarks>
/// M3 adds the recipe tables and replaces the default zero-returning
/// implementation with real queries. M2 tests register counting fakes through
/// the test host. Implementations must not throw for an id that does not exist:
/// the caller decides 404 versus in-use.
/// </remarks>
public interface IReferenceUsageChecker
{
    /// <summary>Number of recipe ingredients that reference the unit.</summary>
    Task<int> CountUnitReferencesAsync(long unitId, CancellationToken cancellationToken = default);

    /// <summary>Number of recipe ingredients that reference the ingredient.</summary>
    Task<int> CountIngredientReferencesAsync(long ingredientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reassigns every recipe reference from the source ingredient to the
    /// target ingredient and returns the number of reassigned references.
    /// </summary>
    Task<int> ReassignIngredientReferencesAsync(long sourceId, long targetId, CancellationToken cancellationToken = default);

    /// <summary>Number of recipes that reference the protein.</summary>
    Task<int> CountProteinReferencesAsync(long proteinId, CancellationToken cancellationToken = default);

    /// <summary>Number of recipes that reference the tag.</summary>
    Task<int> CountTagReferencesAsync(long tagId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pre-M3 default implementation of the usage seam: the recipe tables do not
/// exist yet, so every count is zero. It keeps the delete routes functional and
/// testable until M3 replaces this registration with real queries.
/// </summary>
public sealed class DefaultReferenceUsageChecker : IReferenceUsageChecker
{
    /// <inheritdoc />
    public Task<int> CountUnitReferencesAsync(
        long unitId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    public Task<int> CountIngredientReferencesAsync(
        long ingredientId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    public Task<int> ReassignIngredientReferencesAsync(
        long sourceId,
        long targetId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    public Task<int> CountProteinReferencesAsync(
        long proteinId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }

    /// <inheritdoc />
    public Task<int> CountTagReferencesAsync(
        long tagId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(0);
    }
}
