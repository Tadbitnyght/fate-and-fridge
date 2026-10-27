using System.Globalization;

namespace FateAndFridge.Api.LiveSync;

/// <summary>
/// Typed constructors for the M9 event matrix (docs/M9_PLAN.md §1.5/§2.2) so
/// every write path names the same entity string and populates the optional
/// <c>entity</c>/<c>id</c>/<c>operation_id</c>/<c>actor_user_id</c> fields
/// consistently. Each helper publishes immediately; callers invoke them only
/// after the write has committed successfully (never on a failed, rolled-back,
/// or validation-error request).
/// </summary>
public static class LiveSyncPublisherExtensions
{
    /// <summary>Coarse entity name for calendar events.</summary>
    public const string CalendarEntity = "calendar";

    /// <summary>Coarse entity name for grocery events.</summary>
    public const string GroceriesEntity = "groceries";

    /// <summary>Coarse entity name for recipe events.</summary>
    public const string RecipesEntity = "recipes";

    /// <summary>Coarse entity name for household/security settings events.</summary>
    public const string SettingsEntity = "settings";

    /// <summary>Reference-data entity names broadcast as <c>settings.updated</c>.</summary>
    public const string DepartmentsEntity = "departments";

    /// <inheritdoc cref="DepartmentsEntity" />
    public const string UnitsEntity = "units";

    /// <inheritdoc cref="DepartmentsEntity" />
    public const string IngredientsEntity = "ingredients";

    /// <inheritdoc cref="DepartmentsEntity" />
    public const string ProteinsEntity = "proteins";

    /// <inheritdoc cref="DepartmentsEntity" />
    public const string TagsEntity = "tags";

    /// <summary>Broadcasts <c>calendar.updated</c> for a committed calendar write.</summary>
    public static Task CalendarUpdatedAsync(
        this ILiveSyncPublisher publisher,
        long? actorUserId,
        long? entryId = null,
        string? operationId = null,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            LiveSyncEvent.Create(
                LiveSyncEventType.CalendarUpdated,
                entity: CalendarEntity,
                id: ToId(entryId),
                operationId: operationId,
                actorUserId: actorUserId),
            cancellationToken);

    /// <summary>Broadcasts <c>calendar.lock_changed</c> for a committed lock toggle.</summary>
    public static Task CalendarLockChangedAsync(
        this ILiveSyncPublisher publisher,
        long? actorUserId,
        long? entryId = null,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            LiveSyncEvent.Create(
                LiveSyncEventType.CalendarLockChanged,
                entity: CalendarEntity,
                id: ToId(entryId),
                actorUserId: actorUserId),
            cancellationToken);

    /// <summary>Broadcasts <c>groceries.updated</c> keyed by a manual item id.</summary>
    public static Task GroceriesUpdatedAsync(
        this ILiveSyncPublisher publisher,
        long? actorUserId,
        long? manualItemId,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            LiveSyncEvent.Create(
                LiveSyncEventType.GroceriesUpdated,
                entity: GroceriesEntity,
                id: ToId(manualItemId),
                actorUserId: actorUserId),
            cancellationToken);

    /// <summary>Broadcasts <c>groceries.updated</c> keyed by a line identity (check state).</summary>
    public static Task GroceriesUpdatedAsync(
        this ILiveSyncPublisher publisher,
        long? actorUserId,
        string? identity,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            LiveSyncEvent.Create(
                LiveSyncEventType.GroceriesUpdated,
                entity: GroceriesEntity,
                id: identity,
                actorUserId: actorUserId),
            cancellationToken);

    /// <summary>Broadcasts <c>recipe.updated</c> for a committed recipe/image write.</summary>
    public static Task RecipeUpdatedAsync(
        this ILiveSyncPublisher publisher,
        long? actorUserId,
        long recipeId,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            LiveSyncEvent.Create(
                LiveSyncEventType.RecipeUpdated,
                entity: RecipesEntity,
                id: ToId(recipeId),
                actorUserId: actorUserId),
            cancellationToken);

    /// <summary>
    /// Broadcasts <c>settings.updated</c> for household/security settings or a
    /// reference-data write; <paramref name="entity"/> is the coarse resource name.
    /// </summary>
    public static Task SettingsUpdatedAsync(
        this ILiveSyncPublisher publisher,
        string entity,
        long? actorUserId,
        long? id = null,
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            LiveSyncEvent.Create(
                LiveSyncEventType.SettingsUpdated,
                entity: entity,
                id: ToId(id),
                actorUserId: actorUserId),
            cancellationToken);

    private static string? ToId(long? id) => id?.ToString(CultureInfo.InvariantCulture);
}
