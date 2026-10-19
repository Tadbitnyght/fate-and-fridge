using System.Globalization;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Outcome of an M12-08 hard recipe delete (docs/M12_PLAN.md §1.10, §2.2,
/// docs/DATABASE.md §14).
/// </summary>
public abstract record RecipeHardDeleteOutcome
{
    /// <summary>
    /// The delete committed. <see cref="RemovedPlannedEntries"/> counts the
    /// today/future entries that were removed, <see cref="TombstonedEntries"/>
    /// the past entries whose recipe reference was nulled with the snapshot
    /// preserved, and <see cref="RemovedImages"/> the image row/file removed.
    /// </summary>
    public sealed record Success(
        long RecipeId,
        int RemovedPlannedEntries,
        int TombstonedEntries,
        int RemovedImages) : RecipeHardDeleteOutcome;

    /// <summary>The shared dangerous-action guard rejected the request.</summary>
    public sealed record ValidationFailed(
        DangerousActionValidation.Failure Failure) : RecipeHardDeleteOutcome;

    /// <summary>No recipe has the requested id.</summary>
    public sealed record NotFound : RecipeHardDeleteOutcome;
}

/// <summary>
/// M12-08 irreversibility delivery: an Admin may hard-delete a recipe at any
/// time (docs/M12_PLAN.md §1.10, docs/DATABASE.md §14; docs/SECURITY.md §4).
/// </summary>
/// <remarks>
/// <para>
/// One transaction performs the whole workflow: the recipe's image rows and its
/// own row are removed, every referencing calendar entry gets durable snapshot
/// text before its reference is cleared, entries dated <b>today or later</b> are
/// removed (nothing stays planned against a deleted recipe), and <b>past</b>
/// entries are tombstoned (<c>recipe_id</c> becomes null while
/// <c>snapshot_recipe_name</c>/<c>snapshot_display_text</c> are preserved and
/// <c>include_groceries</c> stays true, the M11 guarantee), and exactly one
/// <c>recipe.hard_deleted</c> audit row records the before-image, the reason,
/// and the affected counts. There is no undo and no soft-delete flag.
/// </para>
/// <para>
/// The caller (the endpoint) has already resolved the actor; the
/// <see cref="DangerousActionGuard"/> confirmation is the exact recipe name,
/// which is only known once the recipe is loaded, so the guard runs here. A
/// missing recipe is reported before the guard so an unknown id is always
/// <c>404</c>.
/// </para>
/// </remarks>
public sealed class RecipeHardDeleteService(
    AppDbContext dbContext,
    HouseholdClock clock,
    RecipeImageService images,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<RecipeHardDeleteService> logger)
{
    /// <summary>
    /// First key of the two-key transaction-scoped advisory lock that serializes
    /// concurrent hard deletes of the same recipe (distinct from the per-date
    /// calendar locks, which use the single-key form).
    /// </summary>
    private const int AdvisoryLockNamespace = 1208;

    /// <summary>
    /// Irreversibly deletes the recipe and reconciles its calendar references,
    /// image, and audit row in one transaction.
    /// </summary>
    public async Task<RecipeHardDeleteOutcome> DeleteAsync(
        long recipeId,
        string? reason,
        string? confirmation,
        string? ipAddress,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Serialize two hard deletes of the same recipe so the loser reports 404
        // instead of racing the delete (docs/M12_PLAN.md §4 concurrency).
        await LockRecipeAsync(recipeId, cancellationToken).ConfigureAwait(false);

        var recipe = await dbContext.Recipes
            .SingleOrDefaultAsync(candidate => candidate.Id == recipeId, cancellationToken)
            .ConfigureAwait(false);
        if (recipe is null)
        {
            return new RecipeHardDeleteOutcome.NotFound();
        }

        // The typed confirmation is the exact recipe name (docs/M12_PLAN.md
        // §1.11), ordinal after trimming; the reason is required and bounded.
        var validation = DangerousActionGuard.Validate(reason, confirmation, recipe.Name);
        if (validation is DangerousActionValidation.Failure guardFailure)
        {
            return new RecipeHardDeleteOutcome.ValidationFailed(guardFailure);
        }

        var trimmedReason = ((DangerousActionValidation.Success)validation).Reason;

        var ingredientCount = await dbContext.RecipeIngredients
            .CountAsync(ingredient => ingredient.RecipeId == recipeId, cancellationToken)
            .ConfigureAwait(false);
        var proteinCount = await dbContext.RecipeProteins
            .CountAsync(link => link.RecipeId == recipeId, cancellationToken)
            .ConfigureAwait(false);
        var tagCount = await dbContext.RecipeTags
            .CountAsync(link => link.RecipeId == recipeId, cancellationToken)
            .ConfigureAwait(false);

        var beforeJson = JsonSerializer.Serialize(new
        {
            id = recipe.Id,
            name = recipe.Name,
            status = EnumTextConverters.RecipeStatusToText(recipe.Status),
            is_favorite = recipe.IsFavorite,
            is_vegan = recipe.IsVegan,
            is_vegetarian = recipe.IsVegetarian,
            ingredient_count = ingredientCount,
            protein_count = proteinCount,
            tag_count = tagCount,
        });

        // Stage the image row removal (and remember its file) before the recipe
        // delete; the file itself is deleted best-effort only after the commit.
        var imagePath = await images
            .StageRemovalForRecipeAsync(recipeId, cancellationToken)
            .ConfigureAwait(false);

        var entries = await dbContext.CalendarEntries
            .Where(entry => entry.RecipeId == recipeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var removedPlannedEntries = 0;
        var tombstonedEntries = 0;
        foreach (var entry in entries)
        {
            // "Today or later" is defined by the household-local today: a recipe
            // that no longer exists must not stay planned (docs/M12_PLAN.md
            // §1.10), even when the frozen historical boundary would otherwise
            // call today historical.
            if (entry.MealDate >= context.Today)
            {
                dbContext.CalendarEntries.Remove(entry);
                removedPlannedEntries++;
                continue;
            }

            // Past entries keep rendering after the reference is cleared: the
            // stored snapshot text is made durable first, then recipe_id is
            // nulled. include_groceries is deliberately left untouched so the
            // meal still counts for statistics and meal history (M11 guarantee).
            BackfillSnapshot(entry, recipe.Name);
            entry.RecipeId = null;
            entry.UpdatedAt = now;
            entry.UpdatedByUserId = actorUserId;
            tombstonedEntries++;
        }

        var removedImages = imagePath is null ? 0 : 1;

        // The recipe row is deleted last; its child rows cascade at the database
        // level and calendar references were reconciled above, so no foreign key
        // is broken.
        dbContext.Recipes.Remove(recipe);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.RecipeHardDeleted,
                AuditEntityTypes.Recipe,
                EntityId: recipeId.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                Reason: trimmedReason,
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["removed_planned_entries"] = removedPlannedEntries,
                    ["tombstoned_entries"] = tombstonedEntries,
                    ["removed_images"] = removedImages,
                }),
            cancellationToken).ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (imagePath is not null)
        {
            images.DeleteStoredFile(imagePath);
        }

        logger.LogInformation(
            "Hard-deleted recipe {RecipeId} ({RecipeName}); removed {RemovedEntries} planned entries, tombstoned {TombstonedEntries} past entries, removed {RemovedImages} images",
            recipeId,
            recipe.Name,
            removedPlannedEntries,
            tombstonedEntries,
            removedImages);

        return new RecipeHardDeleteOutcome.Success(
            recipeId,
            removedPlannedEntries,
            tombstonedEntries,
            removedImages);
    }

    /// <summary>
    /// Ensures a tombstoned entry keeps durable display text. An existing
    /// snapshot is never rewritten — historical entries stay frozen to the name
    /// they carried when eaten — so only a missing/blank value is filled from
    /// the recipe being deleted.
    /// </summary>
    private static void BackfillSnapshot(CalendarEntry entry, string recipeName)
    {
        if (string.IsNullOrWhiteSpace(entry.SnapshotRecipeName))
        {
            entry.SnapshotRecipeName = recipeName;
        }

        if (string.IsNullOrWhiteSpace(entry.SnapshotDisplayText))
        {
            entry.SnapshotDisplayText = CalendarService.BuildDisplayText(
                entry.EntryType,
                entry.CustomLabel,
                entry.RestaurantName,
                entry.LocationText,
                recipeName);
        }
    }

    /// <summary>
    /// Takes the transaction-scoped advisory lock for one recipe so concurrent
    /// hard deletes of the same id serialize; released on commit or rollback.
    /// </summary>
    private Task LockRecipeAsync(long recipeId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, {1})",
            new object[] { AdvisoryLockNamespace, (int)recipeId },
            cancellationToken);
}
