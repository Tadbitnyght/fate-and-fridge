using System.Globalization;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Errors;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Outcome of an M12 historical correction.
/// </summary>
public abstract record HistoricalCorrectionOutcome
{
    /// <summary>The correction committed; the day reflects the stored state.</summary>
    public sealed record Success(CalendarDayRecord Day) : HistoricalCorrectionOutcome;

    /// <summary>Field validation failed; <see cref="Code"/> opts into a specific code.</summary>
    public sealed record ValidationFailed(
        IReadOnlyDictionary<string, string[]> FieldErrors,
        string? Code = null) : HistoricalCorrectionOutcome;

    /// <summary>A non-validation failure (not past, locked, or no entry).</summary>
    public sealed record Failure(int StatusCode, string Code, string Message) : HistoricalCorrectionOutcome;
}

/// <summary>
/// Historical correction of one past date. An Admin may change a single household-local past
/// date's meal-defining fields; the write refreshes the frozen snapshot, leaves
/// the lock state untouched, and stages one flagged
/// <c>calendar_entry_history</c> row plus one
/// <c>calendar.historical_correction</c> audit row in the same transaction as
/// the entry update. The per-type field rules and the snapshot computation are
/// the shared <see cref="CalendarService"/> helpers, so this workflow cannot
/// drift from the ordinary calendar writes.
/// </summary>
/// <remarks>
/// The caller (the endpoint) has already run the shared
/// <see cref="DangerousActionGuard"/> (reason and the typed date confirmation)
/// the trimmed reason arrives here ready to persist. The ordinary calendar
/// endpoints keep rejecting past dates (<c>CALENDAR_DATE_IN_PAST</c>); this is
/// the only path that may change history.
/// </remarks>
public sealed class HistoricalCorrectionService(
    AppDbContext dbContext,
    HouseholdClock clock,
    CalendarService calendar,
    TimeProvider timeProvider,
    IAuditService auditService)
{
    /// <summary>Corrects the entry on one past date.</summary>
    public async Task<HistoricalCorrectionOutcome> CorrectAsync(
        DateOnly date,
        CalendarWriteRequest request,
        string reason,
        bool lockAcknowledged,
        long? actorUserId,
        string? ipAddress = null,
        CancellationToken cancellationToken = default)
    {
        var context = await clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
        if (!HouseholdClock.IsHistorical(date, context))
        {
            return new HistoricalCorrectionOutcome.Failure(
                StatusCodes.Status409Conflict,
                ErrorCodes.CalendarDateNotPast,
                "Only past dates can be corrected. Use the normal calendar endpoints for today and future dates.");
        }

        var now = timeProvider.GetUtcNow();

        // The entry, its history row, and the audit row commit atomically
        // The per-date transaction-scoped advisory
        // lock is the same one M6/M7 use, so a correction cannot race a batch
        // apply or a single-date mutation of the same date; it is released on commit or rollback.
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockDateAsync(date, cancellationToken).ConfigureAwait(false);

        var entry = await dbContext.CalendarEntries
            .FirstOrDefaultAsync(candidate => candidate.MealDate == date, cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return new HistoricalCorrectionOutcome.Failure(
                StatusCodes.Status404NotFound,
                ErrorCodes.NotFound,
                "There is no entry on this date to correct.");
        }

        if (entry.IsLocked && !lockAcknowledged)
        {
            return new HistoricalCorrectionOutcome.Failure(
                StatusCodes.Status409Conflict,
                ErrorCodes.CalendarDateLocked,
                "This date is locked. Acknowledge the lock to correct it.");
        }

        var (fields, errors) = await CalendarService.ValidateWriteRequestAsync(
                dbContext,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (fields is null)
        {
            return new HistoricalCorrectionOutcome.ValidationFailed(
                errors,
                CalendarService.RequiredCode(errors));
        }

        // Refresh the frozen snapshot from the recipe at correction time, and
        // leave the lock state as stored.
        var beforeJson = CalendarService.SnapshotJson(entry);
        CalendarService.ApplyFields(entry, fields, now, actorUserId);
        var afterJson = CalendarService.SnapshotJson(entry);

        CalendarService.StageHistoryRow(
            dbContext,
            entry.Id,
            entry.MealDate,
            beforeJson,
            afterJson,
            actorUserId,
            now,
            isHistoricalCorrection: true,
            changeReason: reason);

        await auditService.WriteAsync(
            new AuditEventEntry(
                AuditEventTypes.CalendarHistoricalCorrection,
                AuditEntityTypes.CalendarEntry,
                EntityId: entry.Id.ToString(CultureInfo.InvariantCulture),
                BeforeJson: beforeJson,
                AfterJson: afterJson,
                Reason: reason,
                IpAddress: ipAddress,
                ActorUserId: actorUserId,
                Metadata: CalendarService.MealDateMetadata(entry.MealDate)),
            cancellationToken).ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Reuse the M5 read path so the returned day document (display text
        // recipe summary, repeat warning, is_historical/can_edit) matches every
        // other calendar write exactly.
        var day = await calendar.GetDayAsync(date, cancellationToken).ConfigureAwait(false);
        return new HistoricalCorrectionOutcome.Success(day);
    }

    /// <summary>
    /// Takes the per-date transaction-scoped advisory lock so concurrent apply
    /// and single-date mutations of the same date serialize
    /// The lock is released on commit/rollback.
    /// </summary>
    private Task LockDateAsync(DateOnly date, CancellationToken cancellationToken) =>
        AdvisoryLocks.LockDateAsync(dbContext.Database, date, cancellationToken);
}
