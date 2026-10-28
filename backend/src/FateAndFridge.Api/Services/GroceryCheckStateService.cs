using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The household-shared checkbox state for one grocery identity
/// the identity hash plus the resolved flag.
/// </summary>
public sealed record GroceryCheckStateRecord(
    string Identity,
    bool Checked,
    DateTimeOffset? CheckedAt,
    long? CheckedByUserId);

/// <summary>
/// Household-shared check/uncheck of a grocery line by its stable
/// <see cref="GroceryItemIdentity"/>. One row per
/// identity means any member's check is visible to all and repeated calls are
/// idempotent. A concurrent first write that loses the unique-hash race is
/// retried as an update instead of surfacing a 500.
/// </summary>
public sealed class GroceryCheckStateService(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>The unique index that guards a single row per identity.</summary>
    private const string IdentityIndexName = "ix_grocery_check_state_item_identity_hash";

    /// <summary>
    /// Sets the shared checked flag for <paramref name="identity"/>, creating the
    /// row on first use. <c>checked_at</c> is set when checking and cleared when
    /// unchecking; <c>checked_by_user_id</c> records the actor of the last check
    /// or uncheck (null for the single-user system actor).
    /// </summary>
    public async Task<GroceryCheckStateRecord> SetCheckedAsync(
        string identity,
        bool isChecked,
        long? actorUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var now = timeProvider.GetUtcNow();
        var row = await dbContext.GroceryCheckStates
            .SingleOrDefaultAsync(state => state.ItemIdentityHash == identity, cancellationToken)
            .ConfigureAwait(false);
        if (row is not null)
        {
            Apply(row, isChecked, actorUserId, now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(row);
        }

        var created = new GroceryCheckState { ItemIdentityHash = identity };
        Apply(created, isChecked, actorUserId, now);
        dbContext.GroceryCheckStates.Add(created);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(created);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolationOn(exception, IdentityIndexName))
        {
            // A concurrent first write won the unique hash; detach our transient
            // row and apply the change to the winner rather than returning 500.
            dbContext.Entry(created).State = EntityState.Detached;
            var winner = await dbContext.GroceryCheckStates
                .SingleAsync(state => state.ItemIdentityHash == identity, cancellationToken)
                .ConfigureAwait(false);
            Apply(winner, isChecked, actorUserId, now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToRecord(winner);
        }
    }

    private static void Apply(
        GroceryCheckState row,
        bool isChecked,
        long? actorUserId,
        DateTimeOffset now)
    {
        row.Checked = isChecked;
        row.CheckedByUserId = actorUserId;
        row.CheckedAt = isChecked ? now : null;
        row.UpdatedAt = now;
    }

    private static GroceryCheckStateRecord ToRecord(GroceryCheckState row) =>
        new(row.ItemIdentityHash, row.Checked, row.CheckedAt, row.CheckedByUserId);
}
