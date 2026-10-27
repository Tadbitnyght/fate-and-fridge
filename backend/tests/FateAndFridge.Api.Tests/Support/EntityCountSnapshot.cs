using FateAndFridge.Api.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Captures row counts across every M1–M4 entity family so import tests can
/// prove the draft path writes nothing anywhere. Use before/after a request and
/// assert the snapshots are equal.
/// </summary>
internal static class EntityCountSnapshot
{
    public static async Task<IReadOnlyDictionary<string, int>> CaptureAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["application_settings"] = await context.ApplicationSettings.CountAsync(cancellationToken),
            ["audit_events"] = await context.AuditEvents.CountAsync(cancellationToken),
            ["auth_events"] = await context.AuthEvents.CountAsync(cancellationToken),
            ["grocery_departments"] = await context.GroceryDepartments.CountAsync(cancellationToken),
            ["household_settings"] = await context.HouseholdSettings.CountAsync(cancellationToken),
            ["ingredient_aliases"] = await context.IngredientAliases.CountAsync(cancellationToken),
            ["ingredients"] = await context.Ingredients.CountAsync(cancellationToken),
            ["proteins"] = await context.Proteins.CountAsync(cancellationToken),
            ["recipe_images"] = await context.RecipeImages.CountAsync(cancellationToken),
            ["recipe_ingredient_groups"] = await context.RecipeIngredientGroups.CountAsync(cancellationToken),
            ["recipe_ingredients"] = await context.RecipeIngredients.CountAsync(cancellationToken),
            ["recipe_instruction_sections"] = await context.RecipeInstructionSections.CountAsync(cancellationToken),
            ["recipe_instruction_steps"] = await context.RecipeInstructionSteps.CountAsync(cancellationToken),
            ["recipe_proteins"] = await context.RecipeProteins.CountAsync(cancellationToken),
            ["recipe_tags"] = await context.RecipeTags.CountAsync(cancellationToken),
            ["recipes"] = await context.Recipes.CountAsync(cancellationToken),
            ["tags"] = await context.Tags.CountAsync(cancellationToken),
            ["unit_aliases"] = await context.UnitAliases.CountAsync(cancellationToken),
            ["units"] = await context.Units.CountAsync(cancellationToken),
            ["user_permissions"] = await context.UserPermissions.CountAsync(cancellationToken),
            ["user_sessions"] = await context.UserSessions.CountAsync(cancellationToken),
            ["users"] = await context.Users.CountAsync(cancellationToken),
        };

        return counts;
    }

    /// <summary>Fails when any entity family's row count changed between snapshots.</summary>
    public static void ShouldBeUnchanged(
        IReadOnlyDictionary<string, int> before,
        IReadOnlyDictionary<string, int> after)
    {
        var changed = before
            .Where(pair => !after.TryGetValue(pair.Key, out var count) || count != pair.Value)
            .Select(pair => after.TryGetValue(pair.Key, out var count)
                ? $"{pair.Key}: {pair.Value} -> {count}"
                : $"{pair.Key}: {pair.Value} -> missing")
            .ToArray();

        changed.Should().BeEmpty(
            "the import path must not write any entity family (changed: {0})",
            string.Join(", ", changed));
    }
}
