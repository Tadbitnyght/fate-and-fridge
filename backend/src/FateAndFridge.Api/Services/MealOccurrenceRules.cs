using System.Linq.Expressions;
using FateAndFridge.Api.Domain;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The shared occurrence rules for the statistics and meal-history reads
/// The household overview, the
/// per-recipe metrics, and the meal-history browser all derive
/// from these helpers so the "eaten vs planned" split and the
/// recipe-occurrence eligibility can never drift.
/// </summary>
/// <remarks>
/// <para>
/// <b>Occurrence.</b> An occurrence is any calendar entry that carries a recipe.
/// M8's grocery projection filters on
/// <c>IncludeGroceries &amp;&amp; RecipeId != null</c>
/// (<see cref="GroceryListService"/> <c>BuildListAsync</c>) because it needs a
/// live recipe to join its ingredient rows. Statistics must keep counting a meal
/// after M12 hard-deletes its recipe, so the
/// occurrence predicate here is <see cref="CalendarEntry.IncludeGroceries"/>
/// alone — the flag M5 derives on write from the entry type and any attached
/// recipe — and the grouping identity falls back to the stored snapshot name
/// when <see cref="CalendarEntry.RecipeId"/> is null. This is the same entry
/// population M8 counts, minus M8's live-recipe join requirement.
/// </para>
/// <para>
/// <b>Eaten vs planned.</b> An occurrence is eaten only when its
/// household-local <see cref="CalendarEntry.MealDate"/> is strictly before the
/// household's today; today's dinner is not yet eaten.
/// The household today comes from <see cref="HouseholdClock"/>, never the server
/// wall clock. Planned occurrences are today and later.
/// </para>
/// </remarks>
internal static class MealOccurrenceRules
{
    /// <summary>
    /// EF-translatable predicate for a recipe occurrence — an entry that carries
    /// a recipe.
    /// </summary>
    public static Expression<Func<CalendarEntry, bool>> IsRecipeOccurrence =>
        entry => entry.IncludeGroceries;

    /// <summary>
    /// EF-translatable predicate for an eaten occurrence: strictly before the
    /// household-local <paramref name="householdToday"/>
    /// </summary>
    public static Expression<Func<CalendarEntry, bool>> EatenBefore(DateOnly householdToday) =>
        entry => entry.MealDate < householdToday;

    /// <summary>
    /// In-memory form of the eaten rule, for callers that already projected the
    /// meal date (per-recipe last-eaten, history paging, tests).
    /// </summary>
    public static bool IsEaten(DateOnly mealDate, DateOnly householdToday) =>
        mealDate < householdToday;

    /// <summary>
    /// Stable grouping identity for an occurrence: the live recipe id when one
    /// survives, otherwise the normalized stored snapshot name (a tombstone
    /// hard delete is M12). Returns null only when neither identity exists
    /// which M5's write-time derivation makes unreachable.
    /// </summary>
    public static string? GroupKey(long? recipeId, string? snapshotRecipeName)
    {
        if (recipeId is { } id)
        {
            return $"recipe:{id}";
        }

        var normalized = NormalizeSnapshotName(snapshotRecipeName);
        return normalized is null ? null : $"name:{normalized}";
    }

    /// <summary>
    /// Normalizes a tombstone snapshot name with the shared reference-data
    /// normalizer (trim, collapse whitespace, fold diacritics, invariant
    /// lowercase) so a renamed or re-cased snapshot still groups as one recipe.
    /// Null when the name is blank or unusable.
    /// </summary>
    public static string? NormalizeSnapshotName(string? snapshotRecipeName)
    {
        if (ReferenceDataNormalizer.Normalize(snapshotRecipeName)
            is ReferenceDataNormalizationResult.Success success)
        {
            return success.Normalized;
        }

        return null;
    }

    /// <summary>
    /// The display form of a snapshot name (caller case and diacritics kept
    /// whitespace collapsed). Null when the snapshot is blank or unusable.
    /// </summary>
    public static string? SnapshotDisplayName(string? snapshotRecipeName)
    {
        if (ReferenceDataNormalizer.Normalize(snapshotRecipeName)
            is ReferenceDataNormalizationResult.Success success)
        {
            return success.Display;
        }

        return null;
    }
}
