using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Audit export, the <c>audit_retention_days</c> security setting, and
/// the confirmed retention prune. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class AuditExportRetentionIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string AuditPath = "/api/v1/admin/audit";
    private const string ExportPath = "/api/v1/admin/audit/export";
    private const string PrunePath = "/api/v1/admin/audit/prune";
    private const string SecurityPath = "/api/v1/settings/security";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m12-audit-export-retention-test";
    private const string TestIp = "203.0.113.11";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Csv_export_returns_the_filtered_view_with_a_download_disposition()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var entry = await SeedCalendarEntryAsync(new DateOnly(2026, 11, 10));
        await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry,
            Now.AddMinutes(-10), actorUserId: admin.Id, entityId: entry.Id.ToString(CultureInfo.InvariantCulture));
        await SeedAuditAsync(
            AuditEventTypes.UserCreated, AuditEntityTypes.User,
            Now.AddMinutes(-5), actorUserId: admin.Id);
        await SeedHistoryAsync(AuditEntryHistoryKinds.Reroll, Now.AddMinutes(-1), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        await ClearLoginAuditRowsAsync();

        var response = await GetAsync(
            session.Client,
            $"{ExportPath}?format=csv&event_type={AuditEventTypes.CalendarCreated}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileName.Should().Be("audit-log.csv");

        var csv = await response.Content.ReadAsStringAsync(Ct);
        var rows = csv.Split("\r\n");
        rows[0].Should().Be(
            "id,source,recorded_at,actor_user_id,actor_username,event_type,entity_type," +
            "entity_id,summary,reason,ip_address,before,after,metadata");
        rows.Should().HaveCount(2, "only the filtered audit row is exported");
        rows[1].Should().Contain($",{AuditEventTypes.CalendarCreated},");
        rows[1].Should().Contain(",Calendar entry added,");
    }

    [Fact]
    public async Task Csv_export_neutralises_formulas_and_quotes_commas_and_newlines()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedAuditAsync(
            AuditEventTypes.CalendarUpdated,
            AuditEntityTypes.CalendarEntry,
            Now.AddMinutes(-2),
            actorUserId: admin.Id,
            entityId: "=SUM(A1:A9)",
            reason: "line one,\n\"quoted\" line two");

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        await ClearLoginAuditRowsAsync();

        var csv = await (await GetAsync(session.Client, $"{ExportPath}?format=csv"))
            .Content.ReadAsStringAsync(Ct);

        csv.Should().Contain("'=SUM(A1:A9)", "a leading '=' must never execute in a spreadsheet");
        csv.Should().Contain("\"line one,\n\"\"quoted\"\" line two\"");
    }

    [Fact]
    public async Task Json_export_is_the_same_array_of_items_as_the_viewer()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry,
            Now.AddMinutes(-4), actorUserId: admin.Id, entityId: "7",
            afterJson: """{"entry_type":"eat_out"}""");
        await SeedAuditAsync(
            AuditEventTypes.UserCreated, AuditEntityTypes.User,
            Now.AddMinutes(-3), actorUserId: admin.Id);
        await SeedHistoryAsync(AuditEntryHistoryKinds.Reroll, Now.AddMinutes(-1), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        await ClearLoginAuditRowsAsync();

        var viewer = await ReadJsonAsync(await GetAsync(session.Client, $"{AuditPath}?limit=200"));
        var viewerItems = viewer.GetProperty("items").EnumerateArray().Select(item => item.GetRawText())
            .ToArray();

        var exportResponse = await GetAsync(session.Client, $"{ExportPath}?format=json");
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        exportResponse.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        exportResponse.Content.Headers.ContentDisposition!.FileName.Should().Be("audit-log.json");

        var export = await ReadJsonAsync(exportResponse);
        export.ValueKind.Should().Be(JsonValueKind.Array);
        export.EnumerateArray().Select(item => item.GetRawText()).Should().Equal(viewerItems);
    }

    [Fact]
    public async Task Export_applies_the_source_filter()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry,
            Now.AddMinutes(-3), actorUserId: admin.Id);
        await SeedHistoryAsync(AuditEntryHistoryKinds.Reroll, Now.AddMinutes(-1), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        await ClearLoginAuditRowsAsync();

        var csv = await (await GetAsync(session.Client, $"{ExportPath}?format=csv&source=entry_history"))
            .Content.ReadAsStringAsync(Ct);

        csv.Split("\r\n").Should().HaveCount(2, "only the reroll history row matches");
        csv.Should().Contain(AuditEntryHistoryKinds.RerollEventType);
        csv.Should().NotContain(AuditEventTypes.CalendarCreated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?format=xlsx")]
    [InlineData("?format=CSV")]
    public async Task Export_rejects_a_missing_or_unknown_format(string query)
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await GetAsync(session.Client, $"{ExportPath}{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "format");
    }

    [Fact]
    public async Task Export_over_the_row_cap_is_validation_failed()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedAuditRowsViaSqlAsync(AuditLogQueryService.MaxExportRows + 1);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        await ClearLoginAuditRowsAsync();

        var response = await GetAsync(session.Client, $"{ExportPath}?format=csv");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "export");
    }

    [Fact]
    public async Task Export_is_admin_only()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);
        using var factory = CreateFactory();

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync($"{ExportPath}?format=csv", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        using var member = await SignInAsync(factory, "member", MemberPassword);
        (await member.Client.GetAsync($"{ExportPath}?format=json", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        using var admin = await SignInAsync(factory, "root", AdminPassword);
        (await admin.Client.GetAsync($"{ExportPath}?format=csv", Ct)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Retention_defaults_to_a_year_and_round_trips_through_the_security_settings()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var initial = await ReadJsonAsync(await GetAsync(session.Client, SecurityPath));
        initial.GetProperty("audit_retention_days").GetInt32()
            .Should().Be(SecuritySettingsService.DefaultAuditRetentionDays);

        var patched = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            SecurityPath,
            """{"audit_retention_days":30}""",
            session.CsrfToken);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(patched)).GetProperty("audit_retention_days").GetInt32().Should().Be(30);

        var reloaded = await ReadJsonAsync(await GetAsync(session.Client, SecurityPath));
        reloaded.GetProperty("audit_retention_days").GetInt32().Should().Be(30);
    }

    [Theory]
    [InlineData("""{"audit_retention_days":1}""")]
    [InlineData("""{"audit_retention_days":6}""")]
    [InlineData("""{"audit_retention_days":3651}""")]
    [InlineData("""{"audit_retention_days":-7}""")]
    [InlineData("""{"audit_retention_days":3.5}""")]
    [InlineData("""{"audit_retention_days":"30"}""")]
    public async Task Retention_rejects_values_outside_zero_or_the_bounded_range(string json)
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Patch, SecurityPath, json, session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(response, "audit_retention_days");
    }

    [Fact]
    public async Task Retention_zero_means_keep_forever_and_refuses_to_prune()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry,
            Now.AddYears(-5), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var patched = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            SecurityPath,
            """{"audit_retention_days":0}""",
            session.CsrfToken);
        patched.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(patched)).GetProperty("audit_retention_days").GetInt32().Should().Be(0);

        var prune = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            PrunePath,
            """{"reason":"try anyway","confirmation":"PRUNE"}""",
            session.CsrfToken);

        prune.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AssertFieldErrorAsync(prune, "audit_retention_days");

        // Nothing was removed by the refused prune.
        (await ReadAuditRowsAsync()).Should().Contain(row => row.EventType == AuditEventTypes.CalendarCreated);
    }

    [Fact]
    public async Task Prune_deletes_only_audit_rows_older_than_the_cutoff_and_writes_its_own_row()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        // Retention 7 days: the cutoff is Now - 7 days.
        var cutoff = Now.AddDays(-7);
        var entry = await SeedCalendarEntryAsync(new DateOnly(2026, 11, 1));
        var oldHistory = await SeedHistoryAsync(
            AuditEntryHistoryKinds.Reroll, cutoff.AddDays(-30), actorUserId: admin.Id, calendarEntryId: entry.Id);
        var atCutoff = await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry, cutoff, actorUserId: admin.Id);
        var justOlder = await SeedAuditAsync(
            AuditEventTypes.CalendarUpdated, AuditEntityTypes.CalendarEntry,
            cutoff.AddSeconds(-1), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        await ClearLoginAuditRowsAsync();

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            SecurityPath,
            """{"audit_retention_days":7}""",
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            PrunePath,
            """{"reason":"Retention window has passed","confirmation":"PRUNE"}""",
            session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadJsonAsync(response);
        result.GetProperty("deleted_count").GetInt32().Should().Be(1);
        result.GetProperty("retention_days").GetInt32().Should().Be(7);
        DateTimeOffset.Parse(result.GetProperty("cutoff").GetString()!, CultureInfo.InvariantCulture)
            .Should().Be(cutoff.ToUniversalTime());

        var rows = await ReadAuditRowsAsync();
        // The row exactly at the cutoff is kept; only the second-older row is gone.
        rows.Should().Contain(row => row.Id == atCutoff.Id);
        rows.Should().NotContain(row => row.Id == justOlder.Id);

        var pruneRow = rows.Should().ContainSingle(row => row.EventType == AuditEventTypes.AuditPruned).Subject;
        pruneRow.EntityType.Should().Be(AuditEntityTypes.AuditLog);
        pruneRow.ActorUserId.Should().Be(admin.Id);
        pruneRow.IpAddress.Should().Be(TestIp);
        pruneRow.Reason.Should().Be("Retention window has passed");
        using (var metadata = JsonDocument.Parse(pruneRow.MetadataJson))
        {
            metadata.RootElement.GetProperty("deleted_count").GetInt32().Should().Be(1);
            metadata.RootElement.GetProperty("retention_days").GetInt32().Should().Be(7);
            DateTimeOffset.Parse(
                    metadata.RootElement.GetProperty("cutoff").GetString()!,
                    CultureInfo.InvariantCulture)
                .Should().Be(cutoff.ToUniversalTime());
        }

        // calendar_entry_history is never touched by the audit prune.
        await using (var context = fixture.CreateContext())
        {
            (await context.CalendarEntryHistory.AsNoTracking().CountAsync(Ct)).Should().Be(1);
            (await context.CalendarEntryHistory.AsNoTracking().SingleAsync(Ct)).Id.Should().Be(oldHistory.Id);
            (await context.CalendarEntries.AsNoTracking().CountAsync(Ct)).Should().Be(1);
        }
    }

    [Theory]
    [InlineData("""{"confirmation":"PRUNE"}""", ErrorCodesReasonRequired)]
    [InlineData("""{"reason":"","confirmation":"PRUNE"}""", ErrorCodesReasonRequired)]
    [InlineData("""{"reason":"why","confirmation":"prune"}""", ErrorCodesConfirmationMismatch)]
    [InlineData("""{"reason":"why"}""", ErrorCodesConfirmationMismatch)]
    public async Task Prune_enforces_the_reason_and_confirmation_through_http(string json, string expectedCode)
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedAuditAsync(
            AuditEventTypes.CalendarCreated, AuditEntityTypes.CalendarEntry,
            Now.AddYears(-5), actorUserId: admin.Id);

        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(session.Client, HttpMethod.Post, PrunePath, json, session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(expectedCode);
        // Rejected before any delete: the old row is untouched.
        (await ReadAuditRowsAsync()).Should().Contain(row => row.EventType == AuditEventTypes.CalendarCreated);
    }

    [Fact]
    public async Task Prune_is_admin_only_and_succeeds_for_an_admin()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);
        using var factory = CreateFactory();
        var body = """{"reason":"housekeeping","confirmation":"PRUNE"}""";

        using var anonymous = factory.CreateClient();
        (await SendJsonAsync(anonymous, HttpMethod.Post, PrunePath, body, csrfToken: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        using var member = await SignInAsync(factory, "member", MemberPassword);
        (await SendJsonAsync(member.Client, HttpMethod.Post, PrunePath, body, member.CsrfToken)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        using var admin = await SignInAsync(factory, "root", AdminPassword);
        (await SendJsonAsync(admin.Client, HttpMethod.Post, PrunePath, body, admin.CsrfToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    private const string ErrorCodesReasonRequired = "REASON_REQUIRED";
    private const string ErrorCodesConfirmationMismatch = "CONFIRMATION_MISMATCH";

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

    private const string EmptyMetadataJson = "{}";

    private async Task SeedAuditRowsViaSqlAsync(int count)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO audit_events (event_type, entity_type, created_at, metadata_json)
            SELECT 'seed', 'seed', {Now.AddDays(-400)}, {EmptyMetadataJson}::jsonb
            FROM generate_series(1, {count})
            """,
            Ct);
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(Ct);
    }

    private async Task ClearLoginAuditRowsAsync()
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM audit_events WHERE event_type LIKE 'auth.%'",
            Ct);
    }

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
        long? calendarEntryId = null)
    {
        await using var context = fixture.CreateContext();
        var row = new CalendarEntryHistory
        {
            CalendarEntryId = calendarEntryId,
            MealDate = Today,
            OperationId = Guid.NewGuid(),
            MetadataJson = kind is null ? null : $"{{\"kind\":\"{kind}\"}}",
            ChangedByUserId = actorUserId,
            ChangedAt = at,
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
            HouseholdName = "M12 Export Retention Household",
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

    private static async Task<SignedInClient> SignInAsync(
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

        return new SignedInClient(client, login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);
        return await client.SendAsync(request, Ct);
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

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString()!;

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        var error = (await ReadJsonAsync(response)).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("VALIDATION_FAILED");
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty(field, out _)
            .Should().BeTrue($"field_errors must name '{field}'");
    }

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
