using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The transaction-scoped PostgreSQL advisory locks shared by the calendar and
/// recipe writers (docs/M6_PLAN.md §1.11, docs/M7_PLAN.md §1.12,
/// docs/M12_PLAN.md §4, docs/M14_PLAN.md §1.9). Every writer must derive a key
/// here: two writers that use a different key do not serialize at all, which is
/// exactly the BUG-003/BUG-011 race.
/// </summary>
internal static class AdvisoryLocks
{
    /// <summary>
    /// Namespace for the two-int per-recipe key form, shared by the M12 hard
    /// delete and the M6/M7 writers that insert a calendar reference.
    /// </summary>
    public const int RecipeNamespace = 1208;

    /// <summary>
    /// Serializes every mutation of one household-local calendar date. The key
    /// is the date's day number: stable across servers, time zones, and app
    /// restarts (docs/M7_PLAN.md §1.12).
    /// </summary>
    public static Task LockDateAsync(
        DatabaseFacade database,
        DateOnly date,
        CancellationToken cancellationToken) =>
        database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { (long)date.DayNumber },
            cancellationToken);

    /// <summary>
    /// Serializes a calendar write that references a recipe against
    /// <see cref="RecipeHardDeleteService"/>. An apply/reroll that holds this
    /// lock makes the hard delete wait; the delete then reconciles the calendar
    /// rows the write committed instead of the writer hitting the recipe foreign
    /// key (BUG-011).
    /// </summary>
    public static Task LockRecipeAsync(
        DatabaseFacade database,
        long recipeId,
        CancellationToken cancellationToken) =>
        database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, {1})",
            new object[] { RecipeNamespace, (int)recipeId },
            cancellationToken);
}
