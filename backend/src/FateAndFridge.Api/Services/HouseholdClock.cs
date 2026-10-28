using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The household's calendar frame for a request
/// household-local today, the frozen historical boundary, and the calendar
/// preferences reads/writes need.
/// </summary>
public sealed record HouseholdCalendarContext(
    DateOnly Today,
    DateOnly? HistoricalBoundaryDate,
    int WeekStartsOn,
    int DefaultDinerCount,
    TimeZoneInfo TimeZone);

/// <summary>
/// Computes calendar dates in the household timezone (docs/ARCHITECTURE.md §11).
/// "Today" comes from the injected <see cref="TimeProvider"/>, never the server
/// wall clock, so tests are deterministic. The frozen boundary prevents a date
/// that was already historical from becoming editable again after a timezone
/// change.
/// </summary>
public sealed class HouseholdClock(AppDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Server-enforced planning horizon, in years either side of today.</summary>
    public const int HorizonYears = 5;

    /// <summary>Maximum number of days allowed in one calendar range read.</summary>
    public const int MaxRangeDays = 366;

    /// <summary>Loads the current household calendar context.</summary>
    public async Task<HouseholdCalendarContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.HouseholdSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var timeZone = ResolveTimeZone(settings?.Timezone);
        var today = GetLocalDate(timeZone, timeProvider.GetUtcNow());
        return new HouseholdCalendarContext(
            today,
            settings?.HistoricalBoundaryDate,
            settings?.WeekStartsOn ?? 0,
            settings?.DefaultDinerCount ?? 4,
            timeZone);
    }

    /// <summary>
    /// True when the date is immutable normal-use history. A date at or before
    /// the frozen boundary stays historical even if a timezone change makes it
    /// the current local day, so history can never silently become editable.
    /// </summary>
    public static bool IsHistorical(DateOnly date, HouseholdCalendarContext context) =>
        date < context.Today
        || (context.HistoricalBoundaryDate is { } boundary && date <= boundary);

    /// <summary>Converts an instant to the household-local calendar date.</summary>
    public static DateOnly GetLocalDate(TimeZoneInfo timeZone, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);

    /// <summary>Resolves an IANA id, falling back to UTC if it cannot be loaded.</summary>
    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Utc;
        }

        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out var timeZone)
            ? timeZone
            : TimeZoneInfo.Utc;
    }
}
