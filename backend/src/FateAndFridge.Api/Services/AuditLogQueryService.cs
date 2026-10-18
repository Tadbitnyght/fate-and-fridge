using System.Buffers.Text;
using System.Globalization;
using System.Text;
using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The two row origins of the merged admin audit timeline
/// (docs/M12_PLAN.md §1.5): <c>audit_events</c> rows and reroll
/// <c>calendar_entry_history</c> rows.
/// </summary>
public static class AuditLogSources
{
    /// <summary>An <c>audit_events</c> row (every row, unfiltered).</summary>
    public const string Audit = "audit";

    /// <summary>A <c>calendar_entry_history</c> row carrying reroll/batch metadata.</summary>
    public const string EntryHistory = "entry_history";
}

/// <summary>
/// The four reroll/batch <c>calendar_entry_history.metadata_json.kind</c> values
/// (docs/DATABASE.md §7) and their canonical viewer event types
/// (docs/M12_PLAN.md §1.4, §1.5). Rerolls are deliberately absent from
/// <c>audit_events</c>; the viewer maps the history kind to one of these event
/// types so a reroll row reads like an audit row.
/// </summary>
public static class AuditEntryHistoryKinds
{
    public const string Reroll = "reroll";

    public const string BatchReroll = "batch_reroll";

    public const string FillEmpty = "fill_empty";

    public const string Undo = "undo";

    public const string RerollEventType = "calendar.reroll";

    public const string BatchRerollEventType = "calendar.batch_reroll";

    public const string FillEmptyEventType = "calendar.fill_empty";

    public const string UndoEventType = "calendar.undo";

    /// <summary>The entity type every reroll history row is projected as.</summary>
    public const string EntityType = AuditEntityTypes.CalendarEntry;

    /// <summary>The canonical event type for a stored history kind, or null.</summary>
    public static string? EventTypeForKind(string? kind) => kind switch
    {
        Reroll => RerollEventType,
        BatchReroll => BatchRerollEventType,
        FillEmpty => FillEmptyEventType,
        Undo => UndoEventType,
        _ => null,
    };

    /// <summary>True when a stored history kind is a reroll/batch record.</summary>
    public static bool IsRerollKind(string? kind) => EventTypeForKind(kind) is not null;
}

/// <summary>
/// Short server-computed, human-readable one-line summaries for the audit
/// viewer (docs/M12_PLAN.md §2.2). The mapping is deterministic and
/// culture-independent; an event type outside the explicit map falls back to a
/// humanized <c>entity_type + event_type</c> phrase, so a new open-string event
/// type still renders sensibly without a service change.
/// </summary>
public static class AuditEventSummaries
{
    private static readonly IReadOnlyDictionary<string, string> Known =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AuditEventTypes.CalendarCreated] = "Calendar entry added",
            [AuditEventTypes.CalendarUpdated] = "Calendar entry changed",
            [AuditEventTypes.CalendarDeleted] = "Calendar entry removed",
            [AuditEventTypes.CalendarLocked] = "Calendar entry locked",
            [AuditEventTypes.CalendarUnlocked] = "Calendar entry unlocked",
            [AuditEntryHistoryKinds.RerollEventType] = "Meal rerolled",
            [AuditEntryHistoryKinds.BatchRerollEventType] = "Batch reroll applied",
            [AuditEntryHistoryKinds.FillEmptyEventType] = "Empty days filled",
            [AuditEntryHistoryKinds.UndoEventType] = "Calendar change undone",
            [AuditEventTypes.AuthLoginSucceeded] = "Login succeeded",
            [AuditEventTypes.AuthLoginFailed] = "Login failed",
            [AuditEventTypes.AuthLogout] = "Logged out",
            [AuditEventTypes.SettingsHouseholdUpdated] = "Household settings updated",
            [AuditEventTypes.SettingsSecurityUpdated] = "Security settings updated",
            [AuditEventTypes.SettingsAuthModeSwitched] = "Authentication mode switched",
            [AuditEventTypes.GroceriesManualItemAdded] = "Manual grocery item added",
            [AuditEventTypes.GroceriesManualItemUpdated] = "Manual grocery item updated",
            [AuditEventTypes.GroceriesManualItemRemoved] = "Manual grocery item removed",
            // Reserved for M12-06/M12-08; harmless before they are emitted.
            ["calendar.historical_correction"] = "Historical calendar correction",
            ["recipe.hard_deleted"] = "Recipe permanently deleted",
        };

    /// <summary>Returns the summary line for one event type and entity type.</summary>
    public static string Summarize(string eventType, string entityType)
    {
        if (Known.TryGetValue(eventType, out var summary))
        {
            return summary;
        }

        return $"{Humanize(entityType)} {Humanize(eventType)}";
    }

    /// <summary>
    /// Turns a machine value into title-like words without relying on
    /// culture-sensitive casing (the API runs under invariant globalization).
    /// </summary>
    private static string Humanize(string value)
    {
        var parts = value.Split(['.', '_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                builder.Append(part, 1, part.Length - 1);
            }
        }

        return builder.ToString();
    }
}

/// <summary>The raw audit query values, straight from the query string.</summary>
public sealed record AuditLogQueryRequest(
    string? EventType,
    string? EntityType,
    string? ActorUserId,
    string? Source,
    string? From,
    string? To,
    string? Limit,
    string? Cursor);

/// <summary>One merged timeline row; the JSON payloads are still raw strings.</summary>
public sealed record AuditLogItemRecord(
    long Id,
    string Source,
    DateTimeOffset RecordedAt,
    long? ActorUserId,
    string? ActorUsername,
    string EventType,
    string EntityType,
    string? EntityId,
    string Summary,
    string? Reason,
    string? IpAddress,
    string? BeforeJson,
    string? AfterJson,
    string? MetadataJson);

/// <summary>One newest-first page plus the opaque cursor for the next page.</summary>
public sealed record AuditLogPageRecord(IReadOnlyList<AuditLogItemRecord> Items, string? NextCursor);

/// <summary>An audit read: one page, or the invalid fields.</summary>
public abstract record AuditLogQueryOutcome
{
    private AuditLogQueryOutcome()
    {
    }

    public sealed record Success(AuditLogPageRecord Page) : AuditLogQueryOutcome;

    public sealed record ValidationFailed(IReadOnlyDictionary<string, string[]> FieldErrors)
        : AuditLogQueryOutcome;
}

/// <summary>
/// The read-only, merged admin audit timeline (docs/M12_PLAN.md §1.5, §1.6,
/// §2.1, §2.2): newest-first <c>audit_events</c> rows ∪ the reroll/batch
/// <c>calendar_entry_history</c> rows, with the filters, the deterministic
/// keyset cursor, and the detail projection the Admin viewer needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>One bounded read.</b> A single <c>UNION ALL</c> projection is ordered and
/// limited in the database, with the actor username resolved by a
/// <c>LEFT JOIN</c> in the same statement, so the query count is independent of
/// the page size and there is no per-row lookup (no N+1).
/// </para>
/// <para>
/// <b>Ordering.</b> <c>recorded_at</c> desc, then <c>source</c> desc, then
/// <c>id</c> desc — a total order over both row kinds, so the keyset cursor
/// (<c>recorded_at | source | id</c>, base64url) never skips or repeats a row.
/// </para>
/// <para>
/// <b>Dates.</b> <c>from</c>/<c>to</c> are household-local <c>yyyy-MM-dd</c>
/// dates, inclusive of the whole day, resolved through
/// <see cref="HouseholdClock"/>'s timezone; the wire stays ISO.
/// </para>
/// </remarks>
public sealed class AuditLogQueryService(AppDbContext dbContext, HouseholdClock clock)
{
    /// <summary>The page size when the request omits <c>limit</c>.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page a caller may request.</summary>
    public const int MaxLimit = 200;

    private const string DateFormat = "yyyy-MM-dd";

    private const string CursorFormat = "O";

    // The canonical event type for a reroll history row, from its stored kind.
    private const string KindEventTypeSql =
        "CASE h.metadata_json->>'kind' " +
        "WHEN 'reroll' THEN 'calendar.reroll' " +
        "WHEN 'batch_reroll' THEN 'calendar.batch_reroll' " +
        "WHEN 'fill_empty' THEN 'calendar.fill_empty' " +
        "WHEN 'undo' THEN 'calendar.undo' END";

    private const string AuditBranchSql =
        "SELECT a.id AS id, 'audit'::text AS source, a.created_at AS recorded_at, " +
        "a.actor_user_id AS actor_user_id, u.username AS actor_username, " +
        "a.event_type AS event_type, a.entity_type AS entity_type, " +
        "a.entity_id AS entity_id, a.reason AS reason, a.ip_address AS ip_address, " +
        "a.before_json::text AS before_json, a.after_json::text AS after_json, " +
        "a.metadata_json::text AS metadata_json " +
        "FROM audit_events AS a LEFT JOIN users AS u ON u.id = a.actor_user_id";

    private const string HistoryBranchSql =
        "SELECT h.id AS id, 'entry_history'::text AS source, h.changed_at AS recorded_at, " +
        "h.changed_by_user_id AS actor_user_id, uh.username AS actor_username, " +
        KindEventTypeSql + " AS event_type, 'calendar_entry'::text AS entity_type, " +
        "h.calendar_entry_id::text AS entity_id, h.change_reason AS reason, " +
        "NULL::text AS ip_address, h.before_json::text AS before_json, " +
        "h.after_json::text AS after_json, h.metadata_json::text AS metadata_json " +
        "FROM calendar_entry_history AS h LEFT JOIN users AS uh ON uh.id = h.changed_by_user_id";

    private readonly AppDbContext _dbContext = dbContext;

    private readonly HouseholdClock _clock = clock;

    /// <summary>Reads one page of the merged timeline for the given filters.</summary>
    public async Task<AuditLogQueryOutcome> QueryAsync(
        AuditLogQueryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var limit = ReadLimit(request.Limit, errors);
        var source = ReadSource(request.Source, errors);
        var actorUserId = ReadActorUserId(request.ActorUserId, errors);
        var cursor = ReadCursor(request.Cursor, errors);
        var fromDate = ReadDate(request.From, "from", errors);
        var toDate = ReadDate(request.To, "to", errors);
        if (errors.Count > 0)
        {
            return new AuditLogQueryOutcome.ValidationFailed(errors);
        }

        DateTimeOffset? from = null;
        DateTimeOffset? toExclusive = null;
        if (fromDate is not null || toDate is not null)
        {
            var calendar = await _clock.GetContextAsync(cancellationToken).ConfigureAwait(false);
            // Npgsql writes timestamptz parameters only with a UTC offset, so
            // the household-local day boundaries are normalised to UTC (same
            // instant) before they reach the command.
            if (fromDate is { } start)
            {
                from = AtStartOfLocalDay(start, calendar.TimeZone).ToUniversalTime();
            }

            if (toDate is { } end)
            {
                toExclusive = AtStartOfLocalDay(end.AddDays(1), calendar.TimeZone).ToUniversalTime();
            }

            if (from is { } fromValue && toExclusive is { } toValue && fromValue >= toValue)
            {
                return new AuditLogQueryOutcome.ValidationFailed(FieldError(
                    "to",
                    "to must be on or after from."));
            }
        }

        var rows = await ReadRowsAsync(
                request,
                limit,
                source,
                actorUserId,
                from,
                toExclusive,
                cursor,
                cancellationToken)
            .ConfigureAwait(false);

        var hasMore = rows.Count > limit;
        var pageRows = hasMore ? rows.Take(limit).ToList() : rows;
        var items = pageRows.Select(MapItem).ToArray();
        var nextCursor = hasMore && pageRows.Count > 0
            ? AuditLogCursor.Encode(pageRows[^1].RecordedAt, pageRows[^1].Source, pageRows[^1].Id)
            : null;
        return new AuditLogQueryOutcome.Success(new AuditLogPageRecord(items, nextCursor));
    }

    private async Task<List<AuditLogRow>> ReadRowsAsync(
        AuditLogQueryRequest request,
        int limit,
        string? source,
        long? actorUserId,
        DateTimeOffset? from,
        DateTimeOffset? toExclusive,
        AuditLogCursor? cursor,
        CancellationToken cancellationToken)
    {
        var arguments = new List<object>();
        var auditConditions = new List<string>();
        // Only rows that carry a reroll/batch kind belong to the timeline;
        // ordinary entry edits already appear as calendar audit rows.
        var historyConditions = new List<string>
        {
            "(h.metadata_json->>'kind') IN ('reroll', 'batch_reroll', 'fill_empty', 'undo')",
        };

        string Placeholder(object value)
        {
            arguments.Add(value);
            return "{" + (arguments.Count - 1).ToString(CultureInfo.InvariantCulture) + "}";
        }

        if (request.EventType is { } eventType)
        {
            var placeholder = Placeholder(eventType);
            auditConditions.Add($"a.event_type = {placeholder}");
            historyConditions.Add($"{KindEventTypeSql} = {placeholder}");
        }

        if (request.EntityType is { } entityType)
        {
            var placeholder = Placeholder(entityType);
            auditConditions.Add($"a.entity_type = {placeholder}");
            historyConditions.Add($"{placeholder} = '{AuditEntryHistoryKinds.EntityType}'");
        }

        if (actorUserId is { } actor)
        {
            var placeholder = Placeholder(actor);
            auditConditions.Add($"a.actor_user_id = {placeholder}");
            historyConditions.Add($"h.changed_by_user_id = {placeholder}");
        }

        if (from is { } fromInstant)
        {
            var placeholder = Placeholder(fromInstant);
            auditConditions.Add($"a.created_at >= {placeholder}");
            historyConditions.Add($"h.changed_at >= {placeholder}");
        }

        if (toExclusive is { } toInstant)
        {
            var placeholder = Placeholder(toInstant);
            auditConditions.Add($"a.created_at < {placeholder}");
            historyConditions.Add($"h.changed_at < {placeholder}");
        }

        var branches = new List<string>(capacity: 2);
        if (source is null or AuditLogSources.Audit)
        {
            branches.Add(WithWhere(AuditBranchSql, auditConditions));
        }

        if (source is null or AuditLogSources.EntryHistory)
        {
            branches.Add(WithWhere(HistoryBranchSql, historyConditions));
        }

        var outerConditions = new List<string>();
        if (cursor is { } keyset)
        {
            // Strictly after the cursor in (recorded_at desc, source desc, id desc)
            // order: the tuple comparison for a descending total order.
            var at = Placeholder(keyset.RecordedAt);
            var cursorSource = Placeholder(keyset.Source);
            var cursorId = Placeholder(keyset.Id);
            outerConditions.Add(
                $"(merged.recorded_at < {at} OR (merged.recorded_at = {at} AND " +
                $"(merged.source < {cursorSource} OR " +
                $"(merged.source = {cursorSource} AND merged.id < {cursorId}))))");
        }

        var limitPlaceholder = Placeholder(limit + 1);

        var sql = new StringBuilder()
            .Append("SELECT merged.id, merged.source, merged.recorded_at, merged.actor_user_id, ")
            .Append("merged.actor_username, merged.event_type, merged.entity_type, merged.entity_id, ")
            .Append("merged.reason, merged.ip_address, merged.before_json, merged.after_json, ")
            .Append("merged.metadata_json FROM (")
            .Append(string.Join(" UNION ALL ", branches))
            .Append(") AS merged");
        if (outerConditions.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", outerConditions));
        }

        sql.Append(" ORDER BY merged.recorded_at DESC, merged.source DESC, merged.id DESC");
        sql.Append(" LIMIT ").Append(limitPlaceholder);

        return await _dbContext.Database
            .SqlQueryRaw<AuditLogRow>(sql.ToString(), arguments.ToArray())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string WithWhere(string branch, IReadOnlyList<string> conditions) =>
        conditions.Count == 0 ? branch : $"{branch} WHERE {string.Join(" AND ", conditions)}";

    private static AuditLogItemRecord MapItem(AuditLogRow row) => new(
        row.Id,
        row.Source,
        row.RecordedAt,
        row.ActorUserId,
        row.ActorUsername,
        row.EventType,
        row.EntityType,
        row.EntityId,
        AuditEventSummaries.Summarize(row.EventType, row.EntityType),
        row.Reason,
        row.IpAddress,
        row.BeforeJson,
        row.AfterJson,
        row.MetadataJson);

    private static int ReadLimit(string? raw, IDictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return DefaultLimit;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            || limit < 1
            || limit > MaxLimit)
        {
            errors["limit"] = [$"limit must be an integer between 1 and {MaxLimit}."];
            return DefaultLimit;
        }

        return limit;
    }

    private static string? ReadSource(string? raw, IDictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw is AuditLogSources.Audit or AuditLogSources.EntryHistory)
        {
            return raw;
        }

        errors["source"] = ["source must be 'audit' or 'entry_history'."];
        return null;
    }

    private static long? ReadActorUserId(string? raw, IDictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 1)
        {
            errors["actor_user_id"] = ["actor_user_id must be a positive integer."];
            return null;
        }

        return value;
    }

    private static DateOnly? ReadDate(
        string? raw,
        string field,
        IDictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        if (!DateOnly.TryParseExact(
                raw,
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            errors[field] = [$"{field} must be a yyyy-MM-dd household-local date."];
            return null;
        }

        return date;
    }

    private static AuditLogCursor? ReadCursor(string? raw, IDictionary<string, string[]> errors)
    {
        if (raw is null)
        {
            return null;
        }

        if (!AuditLogCursor.TryDecode(raw, out var cursor))
        {
            errors["cursor"] = ["cursor is not a valid audit cursor."];
            return null;
        }

        return cursor;
    }

    private static DateTimeOffset AtStartOfLocalDay(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    private static Dictionary<string, string[]> FieldError(string field, string message) =>
        new(StringComparer.Ordinal) { [field] = [message] };

    /// <summary>
    /// The opaque, stable, URL-safe keyset cursor: the last returned row's
    /// <c>recorded_at</c>, <c>source</c>, and <c>id</c>. It contains no secret,
    /// so decoding it is a plain parse; a malformed value is a
    /// <c>400 VALIDATION_FAILED</c>.
    /// </summary>
    private sealed record AuditLogCursor(DateTimeOffset RecordedAt, string Source, long Id)
    {
        public static string Encode(DateTimeOffset recordedAt, string source, long id) =>
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Join(
                '|',
                recordedAt.ToUniversalTime().ToString(CursorFormat, CultureInfo.InvariantCulture),
                source,
                id.ToString(CultureInfo.InvariantCulture))));

        public static bool TryDecode(string raw, out AuditLogCursor cursor)
        {
            cursor = null!;
            try
            {
                var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(raw));
                var parts = text.Split('|');
                if (parts.Length != 3)
                {
                    return false;
                }

                if (!DateTimeOffset.TryParseExact(
                        parts[0],
                        CursorFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var recordedAt))
                {
                    return false;
                }

                if (parts[1] is not (AuditLogSources.Audit or AuditLogSources.EntryHistory))
                {
                    return false;
                }

                if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                    || id < 1)
                {
                    return false;
                }

                // A hand-crafted cursor may carry any offset; Npgsql only
                // accepts UTC timestamptz parameters, so normalise here.
                cursor = new AuditLogCursor(recordedAt.ToUniversalTime(), parts[1], id);
                return true;
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>The flattened shape of one merged timeline row.</summary>
    private sealed record AuditLogRow(
        long Id,
        string Source,
        DateTimeOffset RecordedAt,
        long? ActorUserId,
        string? ActorUsername,
        string EventType,
        string EntityType,
        string? EntityId,
        string? Reason,
        string? IpAddress,
        string? BeforeJson,
        string? AfterJson,
        string? MetadataJson);
}
