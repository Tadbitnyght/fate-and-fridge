using System.Buffers.Text;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Merged audit read model and <c>GET /api/v1/admin/audit</c>
/// the newest-first timeline of
/// <c>audit_events</c> plus the reroll/batch <c>calendar_entry_history</c> rows
/// the filters, the keyset cursor, the detail projection, the Admin-only
/// enforcement, and the bounded (no N+1) query shape. Requires a Docker daemon
/// run with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class AuditLogQueryIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string AuditPath = "/api/v1/admin/audit";
    private const string CalendarPath = "/api/v1/calendar";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m12-audit-log-query-test";
    private const string TestIp = "203.0.113.11";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Merged_timeline_contains_audit_and_reroll_history_rows_newest_first()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var entry = await SeedCalendarEntryAsync(new DateOnly(2026, 11, 10));
        var baseTime = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        var first = await SeedAuditAsync(
            AuditEventTypes.CalendarCreated,
            AuditEntityTypes.CalendarEntry,
            baseTime,
            actorUserId: admin.Id,
            entityId: entry.Id.ToString(CultureInfo.InvariantCulture),
            beforeJson: null,
            afterJson: """{"entry_type":"eat_out"}""",
            metadataJson: """{"meal_date":"2026-11-14"}""",
            reason: "planned",
            ipAddress: TestIp);
        var reroll = await SeedHistoryAsync(
            AuditEntryHistoryKinds.Reroll,
            baseTime.AddMinutes(1),
            actorUserId: admin.Id,
            calendarEntryId: entry.Id,
            afterJson: """{"entry_type":"recipe"}""");
        var second = await SeedAuditAsync(
            AuditEventTypes.UserCreated,
            AuditEntityTypes.User,
            baseTime.AddMinutes(2),
            actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var page = await ReadJsonAsync(await GetAuditAsync(session.Client, null));
        var items = Items(page);

        items.Select(item => item.GetProperty("source").GetString()).Should().Equal(
            AuditLogSources.Audit,
            AuditLogSources.EntryHistory,
            AuditLogSources.Audit);
        items.Select(item => item.GetProperty("id").GetInt64()).Should().Equal(
            second.Id,
            reroll.Id,
            first.Id);
        items.Select(item => item.GetProperty("event_type").GetString()).Should().Equal(
            AuditEventTypes.UserCreated,
            AuditEntryHistoryKinds.RerollEventType,
            AuditEventTypes.CalendarCreated);
        page.GetProperty("next_cursor").ValueKind.Should().Be(JsonValueKind.Null);

        // The audit row keeps the stored JSON documents and passes the summary.
        var auditItem = items.Single(item => item.GetProperty("id").GetInt64() == first.Id);
        auditItem.GetProperty("entity_type").GetString().Should().Be(AuditEntityTypes.CalendarEntry);
        auditItem.GetProperty("entity_id").GetString()
            .Should().Be(entry.Id.ToString(CultureInfo.InvariantCulture));
        auditItem.GetProperty("actor_username").GetString().Should().Be("root");
        auditItem.GetProperty("reason").GetString().Should().Be("planned");
        auditItem.GetProperty("ip_address").GetString().Should().Be(TestIp);
        auditItem.GetProperty("before").ValueKind.Should().Be(JsonValueKind.Null);
        auditItem.GetProperty("after").GetProperty("entry_type").GetString().Should().Be("eat_out");
        auditItem.GetProperty("metadata").GetProperty("meal_date").GetString().Should().Be("2026-11-14");
        auditItem.GetProperty("summary").GetString().Should().Be("Calendar entry added");
        auditItem.GetProperty("recorded_at").GetString().Should().NotBeNullOrWhiteSpace();

        // The reroll history row is mapped to the calendar entry entity type and
        // carries the reroll event type and summary; it has no IP address.
        var historyItem = items.Single(item => item.GetProperty("id").GetInt64() == reroll.Id);
        historyItem.GetProperty("source").GetString().Should().Be(AuditLogSources.EntryHistory);
        historyItem.GetProperty("entity_type").GetString()
            .Should().Be(AuditEntryHistoryKinds.EntityType);
        historyItem.GetProperty("entity_id").GetString()
            .Should().Be(entry.Id.ToString(CultureInfo.InvariantCulture));
        historyItem.GetProperty("ip_address").ValueKind.Should().Be(JsonValueKind.Null);
        historyItem.GetProperty("metadata").GetProperty("kind").GetString().Should().Be("reroll");
        historyItem.GetProperty("summary").GetString().Should().Be("Meal rerolled");
    }

    [Fact]
    public async Task The_four_reroll_kinds_map_to_their_calendar_event_types()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var baseTime = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        await SeedHistoryAsync(AuditEntryHistoryKinds.BatchReroll, baseTime, actorUserId: admin.Id);
        await SeedHistoryAsync(AuditEntryHistoryKinds.FillEmpty, baseTime.AddMinutes(1), actorUserId: admin.Id);
        await SeedHistoryAsync(AuditEntryHistoryKinds.Undo, baseTime.AddMinutes(2), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var page = await ReadJsonAsync(await GetAuditAsync(
            session.Client,
            "source=entry_history"));
        var items = Items(page);

        items.Select(item => item.GetProperty("event_type").GetString()).Should().Equal(
            AuditEntryHistoryKinds.UndoEventType,
            AuditEntryHistoryKinds.FillEmptyEventType,
            AuditEntryHistoryKinds.BatchRerollEventType);
        items.Should().OnlyContain(item =>
            item.GetProperty("source").GetString() == AuditLogSources.EntryHistory);
    }

    [Fact]
    public async Task Rows_with_the_same_recorded_at_are_ordered_by_source_then_id_desc()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var at = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var auditFirst = await SeedAuditAsync(
            AuditEventTypes.CalendarUpdated,
            AuditEntityTypes.CalendarEntry,
            at,
            actorUserId: admin.Id);
        var auditSecond = await SeedAuditAsync(
            AuditEventTypes.CalendarLocked,
            AuditEntityTypes.CalendarEntry,
            at,
            actorUserId: admin.Id);
        var history = await SeedHistoryAsync(
            AuditEntryHistoryKinds.Reroll,
            at,
            actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var items = Items(await ReadJsonAsync(await GetAuditAsync(session.Client, null)));

        // Same instant: source desc puts entry_history ahead of audit; inside one
        // source the higher id wins.
        items.Select(item => (item.GetProperty("source").GetString(), item.GetProperty("id").GetInt64()))
            .Should().Equal(
                (AuditLogSources.EntryHistory, history.Id),
                (AuditLogSources.Audit, auditSecond.Id),
                (AuditLogSources.Audit, auditFirst.Id));
    }

    [Fact]
    public async Task Ordinary_entry_edits_appear_once_from_audit_and_are_not_duplicated_from_history()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var put = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"eat_out","restaurant_name":"Pizza Place"}""",
            session.CsrfToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = fixture.CreateContext())
        {
            var historyRows = await context.CalendarEntryHistory
                .Where(row => row.MealDate == Today)
                .ToListAsync(Ct);
            historyRows.Should().ContainSingle("the ordinary edit still writes its undo history row");
            historyRows[0].MetadataJson.Should().BeNull("an ordinary edit carries no reroll kind");
        }

        // The ordinary edit has no reroll kind, so it never appears a second time.
        var historyPage = Items(await ReadJsonAsync(await GetAuditAsync(
            session.Client,
            "source=entry_history")));
        historyPage.Should().BeEmpty();

        // It appears exactly once, as the calendar audit row.
        var calendarItems = Items(await ReadJsonAsync(await GetAuditAsync(
            session.Client,
            $"event_type={AuditEventTypes.CalendarCreated}")));
        calendarItems.Should().ContainSingle();
        calendarItems[0].GetProperty("source").GetString().Should().Be(AuditLogSources.Audit);
    }

    [Fact]
    public async Task Filters_narrow_and_and_combine()
    {
        await SeedAdminHouseholdAsync();
        var adminA = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var adminB = await SeedUserAsync("second", MemberPassword, UserRole.Admin);
        var baseTime = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry,
            baseTime, actorUserId: adminA.Id);
        await SeedAuditAsync(
            AuditEventTypes.UserCreated, AuditEntityTypes.User,
            baseTime.AddMinutes(1), actorUserId: adminA.Id);
        await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry,
            baseTime.AddMinutes(2), actorUserId: adminB.Id);
        await SeedHistoryAsync(
            AuditEntryHistoryKinds.Reroll, baseTime.AddMinutes(3), actorUserId: adminA.Id);
        await SeedHistoryAsync(
            AuditEntryHistoryKinds.Reroll, baseTime.AddMinutes(4), actorUserId: adminB.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        (await CountAsync(session.Client, $"event_type={AuditEventTypes.CalendarCreated}"))
            .Should().Be(2);
        (await CountAsync(session.Client, $"entity_type={AuditEntityTypes.User}"))
            .Should().Be(1);
        (await CountAsync(session.Client, $"actor_user_id={adminA.Id}"))
            .Should().Be(3);
        (await CountAsync(session.Client, "source=entry_history"))
            .Should().Be(2);
        (await CountAsync(session.Client, "source=audit"))
            .Should().Be(3);

        // AND-combined: only adminB's reroll history row matches all three.
        var combined = Items(await ReadJsonAsync(await GetAuditAsync(
            session.Client,
            $"event_type={AuditEntryHistoryKinds.RerollEventType}" +
            $"&actor_user_id={adminB.Id}&source=entry_history")));
        combined.Should().ContainSingle();
        combined[0].GetProperty("event_type").GetString()
            .Should().Be(AuditEntryHistoryKinds.RerollEventType);
    }

    [Fact]
    public async Task The_household_local_date_filter_includes_the_whole_day()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        // America/Chicago is UTC-06:00 in November (CST).
        var beforeDay = new DateTimeOffset(2026, 11, 15, 5, 59, 59, TimeSpan.Zero);   // Nov 14 local
        var dayStart = new DateTimeOffset(2026, 11, 15, 6, 0, 0, TimeSpan.Zero);      // Nov 15 00:00 local
        var dayEnd = new DateTimeOffset(2026, 11, 16, 5, 59, 59, TimeSpan.Zero);      // Nov 15 23:59:59 local
        var afterDay = new DateTimeOffset(2026, 11, 16, 6, 0, 0, TimeSpan.Zero);      // Nov 16 00:00 local

        await SeedAuditAsync(AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry, beforeDay, actorUserId: admin.Id);
        await SeedAuditAsync(AuditEventTypes.CalendarUpdated, AuditEntityTypes.CalendarEntry, dayStart, actorUserId: admin.Id);
        await SeedAuditAsync(AuditEventTypes.CalendarLocked, AuditEntityTypes.CalendarEntry, dayEnd, actorUserId: admin.Id);
        await SeedAuditAsync(AuditEventTypes.CalendarDeleted, AuditEntityTypes.CalendarEntry, afterDay, actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var items = Items(await ReadJsonAsync(await GetAuditAsync(
            session.Client,
            "from=2026-11-15&to=2026-11-15")));
        items.Select(item => item.GetProperty("event_type").GetString()).Should().Equal(
            AuditEventTypes.CalendarLocked,
            AuditEventTypes.CalendarUpdated);
    }

    [Fact]
    public async Task Keyset_paging_walks_the_whole_log_once_without_gaps_or_duplicates()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var baseTime = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var expected = new List<(string Source, long Id)>();
        for (var index = 0; index < 4; index++)
        {
            var audit = await SeedAuditAsync(
                AuditEventTypes.CalendarUpdated,
                AuditEntityTypes.CalendarEntry,
                baseTime.AddMinutes(index * 2),
                actorUserId: admin.Id);
            expected.Add((AuditLogSources.Audit, audit.Id));
            var history = await SeedHistoryAsync(
                AuditEntryHistoryKinds.BatchReroll,
                baseTime.AddMinutes(index * 2 + 1),
                actorUserId: admin.Id);
            expected.Add((AuditLogSources.EntryHistory, history.Id));
        }

        // Newest-first expectation.
        expected.Reverse();

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var visited = new List<(string Source, long Id)>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var query = $"limit=3{(cursor is null ? string.Empty : $"&cursor={Uri.EscapeDataString(cursor)}")}";
            var page = await ReadJsonAsync(await GetAuditAsync(session.Client, query));
            visited.AddRange(Items(page).Select(item =>
                (item.GetProperty("source").GetString()!, item.GetProperty("id").GetInt64())));
            cursor = page.GetProperty("next_cursor").ValueKind == JsonValueKind.Null
                ? null
                : page.GetProperty("next_cursor").GetString();
            pages++;
            pages.Should().BeLessThanOrEqualTo(10, "paging must terminate");
        }
        while (cursor is not null);

        visited.Should().Equal(expected);
        visited.Distinct().Should().HaveCount(expected.Count, "no row may repeat across pages");
        pages.Should().Be(3);
    }

    [Fact]
    public async Task Empty_log_returns_an_empty_page_with_a_null_cursor()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var page = await ReadJsonAsync(await GetAuditAsync(session.Client, null));
        Items(page).Should().BeEmpty();
        page.GetProperty("next_cursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Malformed_query_values_are_validation_failed()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        foreach (var (query, field) in new[]
                 {
                     ("limit=0", "limit"),
                     ("limit=201", "limit"),
                     ("limit=abc", "limit"),
                     ("source=bogus", "source"),
                     ("actor_user_id=abc", "actor_user_id"),
                     ("from=2026-13-01", "from"),
                     ("cursor=%%%", "cursor"),
                     ($"cursor={Uri.EscapeDataString(Base64Url.EncodeToString(Encoding.UTF8.GetBytes("not-a-cursor")))}", "cursor"),
                 })
        {
            var response = await GetAuditAsync(session.Client, query);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"query '{query}'");
            var error = await ReadJsonAsync(response);
            error.GetProperty("error").GetProperty("code").GetString()
                .Should().Be("VALIDATION_FAILED", $"query '{query}'");
            error.GetProperty("error").GetProperty("details").GetProperty("field_errors")
                .TryGetProperty(field, out _).Should().BeTrue($"query '{query}' reports '{field}'");
        }
    }

    [Fact]
    public async Task A_cursor_with_a_non_utc_offset_is_normalised_and_never_500s()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var at = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        await SeedAuditAsync(AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry, at, actorUserId: admin.Id);
        await SeedAuditAsync(AuditEventTypes.CalendarUpdated, AuditEntityTypes.CalendarEntry, at.AddMinutes(1), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        // A hand-crafted cursor (12:00Z expressed as -06:00) must be accepted
        // rather than blow up framing the timestamptz parameter.
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            "2026-11-15T06:02:00.0000000-06:00|audit|9223372036854775807"));
        var response = await GetAuditAsync(session.Client, $"cursor={Uri.EscapeDataString(cursor)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Items(await ReadJsonAsync(response)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Actor_username_is_resolved_by_a_join_and_is_null_for_system_rows()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var at = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        await SeedAuditAsync(AuditEventTypes.SettingsHouseholdUpdated, AuditEntityTypes.Settings, at, actorUserId: admin.Id);
        await SeedAuditAsync(AuditEventTypes.BackupCreated, AuditEntityTypes.Backup, at.AddMinutes(1), actorUserId: null);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var items = Items(await ReadJsonAsync(await GetAuditAsync(session.Client, null)));

        items.Single(item => item.GetProperty("event_type").GetString() == AuditEventTypes.SettingsHouseholdUpdated)
            .GetProperty("actor_username").GetString().Should().Be("root");
        items.Single(item => item.GetProperty("event_type").GetString() == AuditEventTypes.BackupCreated)
            .GetProperty("actor_username").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Anonymous_and_member_are_rejected_while_admin_is_allowed()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);
        using var factory = CreateFactory();

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(AuditPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var member = await SignInAsync(factory, "member", MemberPassword);
        (await member.Client.GetAsync(AuditPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await SignInAsync(factory, "root", AdminPassword);
        (await admin.Client.GetAsync(AuditPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Single_user_system_actor_may_read_the_audit_log()
    {
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        (await client.GetAsync(AuditPath, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Merged_read_is_a_single_bounded_command_independent_of_page_size()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var baseTime = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 12; index++)
        {
            await SeedAuditAsync(
                AuditEventTypes.CalendarUpdated,
                AuditEntityTypes.CalendarEntry,
                baseTime.AddMinutes(index),
                actorUserId: admin.Id);
            await SeedHistoryAsync(
                AuditEntryHistoryKinds.Reroll,
                baseTime.AddMinutes(index).AddSeconds(30),
                actorUserId: admin.Id);
        }

        var oneRow = await CountDirectCommandsAsync(limit: 1);
        var fullPage = await CountDirectCommandsAsync(limit: 200);

        oneRow.Should().Be(1, "the merged read is one UNION ALL command with no per-row lookup");
        fullPage.Should().Be(oneRow, "the query count must not grow with the page size");
    }

    private async Task<int> CountDirectCommandsAsync(int limit)
    {
        var interceptor = new CountingCommandInterceptor();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        await using var context = new AppDbContext(options);
        var service = new AuditLogQueryService(
            context,
            new HouseholdClock(context, new TestTimeProvider(Now)),
            new AuditService(context, new TestTimeProvider(Now), NullLogger<AuditService>.Instance));
        interceptor.Reset();
        var outcome = await service.QueryAsync(
            new AuditLogQueryRequest(
                EventType: null,
                EntityType: null,
                ActorUserId: null,
                Source: null,
                From: null,
                To: null,
                Limit: limit.ToString(CultureInfo.InvariantCulture),
                Cursor: null),
            Ct);
        outcome.Should().BeOfType<AuditLogQueryOutcome.Success>();
        return interceptor.Count;
    }

    private async Task<int> CountAsync(HttpClient client, string query) =>
        Items(await ReadJsonAsync(await GetAuditAsync(client, query))).Count;

    private static IReadOnlyList<JsonElement> Items(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();

    private static async Task<HttpResponseMessage> GetAuditAsync(HttpClient client, string? query)
    {
        var path = query is null ? AuditPath : $"{AuditPath}?{query}";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);
        return await client.SendAsync(request, Ct);
    }

    private ApiFactory CreateFactory(
        TestTimeProvider? clock = null,
        Action<IServiceCollection>? configureServices = null) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock ?? new TestTimeProvider(Now));
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
            configureServices?.Invoke(services);
        });

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<AuditEvent> SeedAuditAsync(
        string eventType,
        string entityType,
        DateTimeOffset at,
        long? actorUserId = null,
        string? entityId = null,
        string? beforeJson = null,
        string? afterJson = null,
        string? metadataJson = "{}",
        string? reason = null,
        string? ipAddress = null)
    {
        await using var context = fixture.CreateContext();
        var row = new AuditEvent
        {
            ActorUserId = actorUserId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            IpAddress = ipAddress,
            CreatedAt = at,
            MetadataJson = metadataJson ?? "{}",
        };
        context.AuditEvents.Add(row);
        await context.SaveChangesAsync(Ct);
        return row;
    }

    private async Task<CalendarEntryHistory> SeedHistoryAsync(
        string? kind,
        DateTimeOffset at,
        long? actorUserId = null,
        long? calendarEntryId = null,
        string? beforeJson = null,
        string? afterJson = null,
        string? reason = null)
    {
        await using var context = fixture.CreateContext();
        var row = new CalendarEntryHistory
        {
            CalendarEntryId = calendarEntryId,
            MealDate = Today,
            OperationId = Guid.NewGuid(),
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            MetadataJson = kind is null ? null : $"{{\"kind\":\"{kind}\"}}",
            ChangedByUserId = actorUserId,
            ChangedAt = at,
            ChangeReason = reason,
            IsHistoricalCorrection = false,
        };
        context.CalendarEntryHistory.Add(row);
        await context.SaveChangesAsync(Ct);
        return row;
    }

    private async Task<CalendarEntry> SeedCalendarEntryAsync(DateOnly date)
    {
        await using var context = fixture.CreateContext();
        var entry = new CalendarEntry
        {
            MealDate = date,
            EntryType = CalendarEntryType.EatOut,
            IncludeGroceries = false,
            SnapshotDisplayText = "Pizza Place",
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.CalendarEntries.Add(entry);
        await context.SaveChangesAsync(Ct);
        return entry;
    }

    private async Task SeedAdminHouseholdAsync()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await MarkSetupCompletedAsync();
    }

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M12 Audit Log Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task MarkSetupCompletedAsync()
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {SeedTimestamp})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            Ct);
    }

    private async Task<User> SeedUserAsync(string username, string password, UserRole role)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password)
    {
        var cookies = new CookieContainer();
        var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);

        var login = await client.SendAsync(request, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        // The login mirror writes an auth.login_succeeded audit row; the
        // read-model assertions below cover only the rows each test seeds.
        await ClearAuthAuditRowsAsync();

        return new SignedInClient(
            client,
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
    }

    private async Task ClearAuthAuditRowsAsync()
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM audit_events WHERE event_type LIKE 'auth.%'",
            Ct);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string? csrfToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    /// <summary>
    /// Counts every executed command so a per-row lookup would show up as a
    /// command count proportional to the page instead of a single read.
    /// </summary>
    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Reset() => Interlocked.Exchange(ref _count, 0);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }
}
