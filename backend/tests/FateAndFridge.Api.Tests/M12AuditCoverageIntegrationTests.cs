using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
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
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M12-01 audit coverage sweep (docs/M12_PLAN.md §1.3, §2.1, §4): the calendar,
/// user/permission, settings, grocery, and auth write paths each stage exactly
/// one <c>audit_events</c> row with the actor, target, and before/after
/// documents; a failed audit write aborts the change; and no plaintext,
/// temporary, or reset password, recovery key, or session token ever reaches a
/// persisted audit payload. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class M12AuditCoverageIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string UsersPath = "/api/v1/users";
    private const string GroceriesManualPath = "/api/v1/groceries/manual";
    private const string GroceriesStaplesPath = "/api/v1/groceries/staples";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string WrongPassword = "definitely not the password";
    private const string TestUserAgent = "m12-audit-coverage-test";
    private const string TestIp = "203.0.113.7";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Calendar_writes_emit_one_row_each_with_actor_target_and_before_after()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = Format(Today);

        var create = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{date}",
            """{"entry_type":"eat_out","restaurant_name":"Pizza Place"}""",
            session.CsrfToken);
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        var update = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{date}",
            """{"entry_type":"fend_for_yourself"}""",
            session.CsrfToken);
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date}/lock",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date}/unlock",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{CalendarPath}/{date}",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var rows = await ReadAuditRowsAsync(row => row.EntityType == AuditEntityTypes.CalendarEntry);
        rows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.CalendarCreated,
            AuditEventTypes.CalendarUpdated,
            AuditEventTypes.CalendarLocked,
            AuditEventTypes.CalendarUnlocked,
            AuditEventTypes.CalendarDeleted);

        rows.Should().OnlyContain(row => row.ActorUserId == admin.Id);
        rows.Should().OnlyContain(row => row.IpAddress == TestIp);
        rows.Should().OnlyContain(row => row.EntityId != null);
        rows.Should().OnlyContain(row =>
            Snapshot(row.MetadataJson)["meal_date"]!.GetValue<string>() == date);

        // Create: no before image, and the after image is the stored entry.
        rows[0].BeforeJson.Should().BeNull();
        Snapshot(rows[0].AfterJson!)["entry_type"]!.GetValue<string>().Should().Be("eat_out");
        Snapshot(rows[0].AfterJson!)["restaurant_name"]!.GetValue<string>().Should().Be("Pizza Place");

        // Update: full-replace before -> after.
        Snapshot(rows[1].BeforeJson!)["entry_type"]!.GetValue<string>().Should().Be("eat_out");
        Snapshot(rows[1].AfterJson!)["entry_type"]!.GetValue<string>().Should().Be("fend_for_yourself");

        // Lock/unlock: the before/after capture the lock transition.
        Snapshot(rows[2].BeforeJson!)["is_locked"]!.GetValue<bool>().Should().BeFalse();
        Snapshot(rows[2].AfterJson!)["is_locked"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(rows[3].BeforeJson!)["is_locked"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(rows[3].AfterJson!)["is_locked"]!.GetValue<bool>().Should().BeFalse();

        // Delete: the before image survives, the after image is absent.
        Snapshot(rows[4].BeforeJson!)["entry_type"]!.GetValue<string>().Should().Be("fend_for_yourself");
        rows[4].AfterJson.Should().BeNull();
    }

    [Fact]
    public async Task Calendar_lock_no_op_writes_no_additional_audit_row()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = Format(Today);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{date}",
            """{"entry_type":"eat_out"}""",
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date}/lock",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Repeating the same lock is a documented no-op.
        (await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date}/lock",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await ReadAuditRowsAsync(row => row.EntityType == AuditEntityTypes.CalendarEntry);
        rows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.CalendarCreated,
            AuditEventTypes.CalendarLocked);
    }

    [Fact]
    public async Task Failed_audit_write_aborts_the_calendar_change()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory(configureServices: services =>
        {
            services.RemoveAll<IAuditService>();
            services.AddScoped<IAuditService>(provider => new FailingCalendarAuditService(
                new AuditService(
                    provider.GetRequiredService<AppDbContext>(),
                    provider.GetRequiredService<TimeProvider>(),
                    NullLogger<AuditService>.Instance)));
        });
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = Format(Today);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{date}",
            """{"entry_type":"eat_out"}""",
            session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.CountAsync(candidate => candidate.MealDate == Today, Ct))
            .Should().Be(0, "the entry must roll back with the failed audit row");
        (await context.CalendarEntryHistory.CountAsync(row => row.MealDate == Today, Ct))
            .Should().Be(0, "the history row must roll back with the failed audit row");
        (await context.AuditEvents.CountAsync(row => row.EntityType == AuditEntityTypes.CalendarEntry, Ct))
            .Should().Be(0);
    }

    [Fact]
    public async Task User_lifecycle_writes_one_row_per_write_with_actor_and_no_secrets()
    {
        await SeedAdminHouseholdAsync();
        var root = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var created = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            UsersPath,
            """{"username":"jane"}""",
            session.CsrfToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdRoot = await ReadJsonAsync(created);
        var userId = createdRoot.GetProperty("id").GetInt64();
        var createTemporaryPassword = createdRoot.GetProperty("temporary_password").GetString()!;

        var renamed = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{UsersPath}/{userId}",
            """{"username":"jane.renamed"}""",
            session.CsrfToken);
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var permissions = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{UsersPath}/{userId}/permissions",
            """{"permissions":["calendar.view"]}""",
            session.CsrfToken);
        permissions.StatusCode.Should().Be(HttpStatusCode.OK);

        var role = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{UsersPath}/{userId}",
            """{"role":"admin"}""",
            session.CsrfToken);
        role.StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{UsersPath}/{userId}/disable",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{UsersPath}/{userId}/enable",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var reset = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{UsersPath}/{userId}/reset-password",
            json: null,
            session.CsrfToken);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var resetTemporaryPassword = (await ReadJsonAsync(reset)).GetProperty("temporary_password").GetString()!;

        var rows = await ReadAuditRowsAsync(row => row.EntityType == AuditEntityTypes.User);
        rows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.UserCreated,
            AuditEventTypes.UserRenamed,
            AuditEventTypes.UserPermissionsChanged,
            AuditEventTypes.UserRoleChanged,
            AuditEventTypes.UserDisabled,
            AuditEventTypes.UserEnabled,
            AuditEventTypes.UserPasswordReset);

        rows.Should().OnlyContain(row => row.ActorUserId == root.Id);
        rows.Should().OnlyContain(row => row.IpAddress == TestIp);
        rows.Should().OnlyContain(row => row.EntityId == userId.ToString(CultureInfo.InvariantCulture));

        // Created: no before image; the default member grants are the after set.
        rows[0].BeforeJson.Should().BeNull();
        Snapshot(rows[0].AfterJson!)["username"]!.GetValue<string>().Should().Be("jane");
        Snapshot(rows[0].AfterJson!)["role"]!.GetValue<string>().Should().Be("member");
        Permissions(rows[0].AfterJson!).Should().Equal(PermissionKeys.MemberViewOnly.OrderBy(key => key, StringComparer.Ordinal));

        // Renamed: only the username changes.
        Snapshot(rows[1].BeforeJson!)["username"]!.GetValue<string>().Should().Be("jane");
        Snapshot(rows[1].AfterJson!)["username"]!.GetValue<string>().Should().Be("jane.renamed");

        // Permissions: before/after are the granted key sets.
        Permissions(rows[2].BeforeJson!).Should().Equal(
            PermissionKeys.MemberViewOnly.OrderBy(key => key, StringComparer.Ordinal));
        Permissions(rows[2].AfterJson!).Should().Equal(PermissionKeys.CalendarView);

        // Role: member -> admin.
        Snapshot(rows[3].BeforeJson!)["role"]!.GetValue<string>().Should().Be("member");
        Snapshot(rows[3].AfterJson!)["role"]!.GetValue<string>().Should().Be("admin");

        Snapshot(rows[4].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(rows[4].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        Snapshot(rows[5].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
        Snapshot(rows[5].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeTrue();

        // Reset: the fact that a temporary password was issued is recorded, the
        // credential itself never is.
        Snapshot(rows[6].MetadataJson)["temporary_password_issued"]!.GetValue<bool>().Should().BeTrue();

        var written = PersistedPayloads(rows);
        written.Should().NotContain(createTemporaryPassword);
        written.Should().NotContain(resetTemporaryPassword);
        written.Should().NotContain("password_hash");
    }

    [Fact]
    public async Task Settings_writes_emit_one_row_each_with_actor()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var household = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            "/api/v1/settings/household",
            """{"household_name":"M12 Household"}""",
            session.CsrfToken);
        household.StatusCode.Should().Be(HttpStatusCode.OK);

        var security = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            "/api/v1/settings/security",
            """{"trusted_proxies":["203.0.113.7"]}""",
            session.CsrfToken);
        security.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await ReadAuditRowsAsync(row => row.EntityType == AuditEntityTypes.Settings);
        rows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.SettingsHouseholdUpdated,
            AuditEventTypes.SettingsSecurityUpdated);
        rows.Should().OnlyContain(row => row.ActorUserId == admin.Id);
        rows.Should().OnlyContain(row => row.IpAddress == TestIp);
        rows[0].EntityId.Should().Be(HouseholdSettingsService.HouseholdSettingsEntityId);
        rows[1].EntityId.Should().Be(SecuritySettingsService.SecuritySettingsEntityId);

        Snapshot(rows[0].BeforeJson!)["household_name"]!.GetValue<string>().Should().Be("M12 Household Audit");
        Snapshot(rows[0].AfterJson!)["household_name"]!.GetValue<string>().Should().Be("M12 Household");
        Snapshot(rows[1].BeforeJson!)["trusted_proxies"]!.AsArray().Should().BeEmpty();
        Snapshot(rows[1].AfterJson!)["trusted_proxies"]![0]!.GetValue<string>().Should().Be("203.0.113.7/32");
    }

    [Fact]
    public async Task Auth_mode_switch_emits_one_system_actor_row_without_credentials()
    {
        await using (var context = fixture.CreateContext())
        {
            context.HouseholdSettings.Add(new HouseholdSettings
            {
                Id = 1,
                HouseholdName = "Single User Household",
                Timezone = "America/Chicago",
                DefaultDinerCount = 4,
                WeekStartsOn = 0,
                StartupPage = StartupPage.Calendar,
                AuthMode = AuthMode.SingleUser,
                CreatedAt = SeedTimestamp,
                UpdatedAt = SeedTimestamp,
            });
            await context.SaveChangesAsync(Ct);
        }

        await MarkSetupCompletedAsync();
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            "/api/v1/settings/auth-mode",
            JsonSerializer.Serialize(new
            {
                auth_mode = "multiuser",
                admin = new { username = "root", password = AdminPassword },
            }),
            csrfToken: null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = (await ReadAuditRowsAsync(r => r.EventType == AuditEventTypes.SettingsAuthModeSwitched))
            .Should().ContainSingle().Subject;
        row.EntityType.Should().Be(AuditEntityTypes.Settings);
        row.EntityId.Should().Be(AuthModeService.AuthModeEntityId);
        row.ActorUserId.Should().BeNull("the single-user system actor performs the switch");
        Snapshot(row.BeforeJson!)["auth_mode"]!.GetValue<string>().Should().Be("single_user");
        Snapshot(row.AfterJson!)["auth_mode"]!.GetValue<string>().Should().Be("multiuser");
        Snapshot(row.MetadataJson)["admin_user_id"]!.GetValue<long>().Should().BeGreaterThan(0);
        PersistedPayloads([row]).Should().NotContain(AdminPassword);
    }

    [Fact]
    public async Task Grocery_manual_item_and_staple_writes_emit_one_row_each()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = Format(Today);

        var createdItem = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            GroceriesManualPath,
            JsonSerializer.Serialize(new
            {
                name = "M12 Milk",
                range_start = date,
                range_days = 7,
                save_for_future = false,
            }),
            session.CsrfToken);
        createdItem.StatusCode.Should().Be(HttpStatusCode.Created);
        var itemId = (await ReadJsonAsync(createdItem)).GetProperty("id").GetInt64();

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{GroceriesManualPath}/{itemId}",
            """{"quantity_numeric":3}""",
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{GroceriesManualPath}/{itemId}",
            json: null,
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var createdStaple = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            GroceriesStaplesPath,
            """{"name":"M12 Staple"}""",
            session.CsrfToken);
        createdStaple.StatusCode.Should().Be(HttpStatusCode.Created);
        var stapleId = (await ReadJsonAsync(createdStaple)).GetProperty("id").GetInt64();

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{GroceriesStaplesPath}/{stapleId}",
            """{"default_quantity_numeric":2}""",
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"{GroceriesStaplesPath}/{stapleId}",
            """{"is_active":false}""",
            session.CsrfToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        var manualRows = await ReadAuditRowsAsync(row => row.EntityType == AuditEntityTypes.GroceryManualItem);
        manualRows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.GroceriesManualItemAdded,
            AuditEventTypes.GroceriesManualItemUpdated,
            AuditEventTypes.GroceriesManualItemRemoved);
        manualRows.Should().OnlyContain(row => row.ActorUserId == admin.Id);
        manualRows.Should().OnlyContain(row => row.EntityId == itemId.ToString(CultureInfo.InvariantCulture));
        manualRows[0].BeforeJson.Should().BeNull();
        Snapshot(manualRows[0].AfterJson!)["name"]!.GetValue<string>().Should().Be("M12 Milk");
        Snapshot(manualRows[1].AfterJson!)["quantity_numeric"]!.GetValue<decimal>().Should().Be(3m);
        manualRows[2].AfterJson.Should().BeNull();

        var stapleRows = await ReadAuditRowsAsync(row => row.EntityType == AuditEntityTypes.GroceryStaple);
        stapleRows.Select(row => row.EventType).Should().Equal(
            AuditEventTypes.GroceriesStapleCreated,
            AuditEventTypes.GroceriesStapleUpdated,
            AuditEventTypes.GroceriesStapleRetired);
        stapleRows.Should().OnlyContain(row => row.ActorUserId == admin.Id);
        stapleRows[0].BeforeJson.Should().BeNull();
        Snapshot(stapleRows[2].BeforeJson!)["is_active"]!.GetValue<bool>().Should().BeTrue();
        Snapshot(stapleRows[2].AfterJson!)["is_active"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Auth_mirror_records_success_failure_and_logout_without_secrets()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();

        // Failed login: the username and outcome category are recorded, never
        // the attempted password.
        using (var anonymous = factory.CreateClient())
        {
            var failure = await SendJsonAsync(
                anonymous,
                HttpMethod.Post,
                "/api/v1/auth/login",
                JsonSerializer.Serialize(new { username = "root", password = WrongPassword }),
                csrfToken: null);
            failure.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using var session = await SignInAsync(factory, "root", AdminPassword);

        var logout = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            "/api/v1/auth/logout",
            json: null,
            session.CsrfToken);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var loginRows = await ReadAuditRowsAsync(row => row.EntityType == AuditEntityTypes.Auth);
        var failed = loginRows.Should().ContainSingle(row => row.EventType == AuditEventTypes.AuthLoginFailed).Subject;
        failed.ActorUserId.Should().Be(admin.Id);
        failed.IpAddress.Should().Be(TestIp);
        Snapshot(failed.MetadataJson)["username"]!.GetValue<string>().Should().Be("root");
        Snapshot(failed.MetadataJson)["reason"]!.GetValue<string>().Should().Be(AuthService.InvalidPasswordReason);

        var succeeded = (await ReadAuditRowsAsync(row => row.EventType == AuditEventTypes.AuthLoginSucceeded))
            .Should().ContainSingle().Subject;
        succeeded.EntityType.Should().Be(AuditEntityTypes.Session);
        succeeded.ActorUserId.Should().Be(admin.Id);
        succeeded.IpAddress.Should().Be(TestIp);

        var logoutRows = await ReadAuditRowsAsync(row => row.EventType == AuditEventTypes.AuthLogout);
        logoutRows.Should().ContainSingle();
        logoutRows[0].EntityType.Should().Be(AuditEntityTypes.Session);
        logoutRows[0].EntityId.Should().Be(succeeded.EntityId);

        // A repeated logout for the same session is an idempotent no-op.
        using (var scope = factory.Services.CreateScope())
        {
            var sessionId = long.Parse(succeeded.EntityId!, CultureInfo.InvariantCulture);
            var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
            (await authService.LogoutAsync(sessionId, admin.Id, "root", TestIp, Ct)).Should().BeTrue();
        }

        var allRows = await ReadAuditRowsAsync();
        (await ReadAuditRowsAsync(row => row.EventType == AuditEventTypes.AuthLogout))
            .Should().ContainSingle("an already-revoked session writes no extra audit row");

        var written = PersistedPayloads(allRows);
        written.Should().NotContain(AdminPassword);
        written.Should().NotContain(WrongPassword);
        written.Should().NotContain(session.RawSessionToken);
        written.Should().NotContain(session.CsrfToken);
    }

    [Fact]
    public async Task No_secret_ever_reaches_a_persisted_audit_payload()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory();
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var created = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            UsersPath,
            JsonSerializer.Serialize(new { username = "smith", password = MemberPassword }),
            session.CsrfToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var targetId = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();

        var reset = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{UsersPath}/{targetId}/reset-password",
            json: null,
            session.CsrfToken);
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        var temporaryPassword = (await ReadJsonAsync(reset)).GetProperty("temporary_password").GetString()!;

        var resetAgain = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{UsersPath}/{targetId}/reset-password",
            json: null,
            session.CsrfToken);
        resetAgain.StatusCode.Should().Be(HttpStatusCode.OK);
        var secondTemporaryPassword = (await ReadJsonAsync(resetAgain))
            .GetProperty("temporary_password").GetString()!;

        var rows = await ReadAuditRowsAsync();
        rows.Should().NotBeEmpty();

        var written = PersistedPayloads(rows);
        foreach (var secret in new[]
                 {
                     AdminPassword, MemberPassword, WrongPassword,
                     temporaryPassword, secondTemporaryPassword,
                     session.RawSessionToken, session.CsrfToken,
                 })
        {
            written.Should().NotContain(secret);
        }

        written.Should().NotContain("argon2");
        written.Should().NotContain("password_hash");
        written.Should().NotContain("token_hash");
        written.Should().NotContain("recovery");
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

    /// <summary>Every persisted text field of every audit row, for secret scans.</summary>
    private static string PersistedPayloads(IReadOnlyList<AuditEvent> rows) =>
        string.Join(
            '\n',
            rows.Select(row => string.Join(
                '\u001f',
                [
                    row.EventType, row.EntityType, row.EntityId, row.BeforeJson, row.AfterJson,
                    row.Reason, row.IpAddress, row.MetadataJson,
                ])));

    private static JsonNode Snapshot(string? json) => JsonNode.Parse(json ?? "{}")!;

    private static string[] Permissions(string? json) =>
        Snapshot(json)["permissions"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(
        Func<AuditEvent, bool>? filter = null)
    {
        await using var context = fixture.CreateContext();
        var rows = await context.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(Ct);
        return filter is null ? rows : rows.Where(filter).ToList();
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
            HouseholdName = "M12 Household Audit",
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

        // The logout endpoint deletes the cookie, so capture the opaque session
        // token now (the secret scan asserts it never reaches an audit row).
        var setCookie = login.Headers
            .GetValues("Set-Cookie")
            .Single(header => header.StartsWith(
                $"{SessionAuthenticationDefaults.CookieName}=",
                StringComparison.Ordinal));
        var rawToken = setCookie
            .Split(';', 2)[0][(SessionAuthenticationDefaults.CookieName.Length + 1)..];

        return new SignedInClient(
            client,
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single(),
            rawToken);
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

    private sealed record SignedInClient(HttpClient Client, string CsrfToken, string RawSessionToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    /// <summary>
    /// Fails only calendar audit staging, so the login mirror still works and
    /// the test observes exactly the calendar transaction rolling back.
    /// </summary>
    private sealed class FailingCalendarAuditService(IAuditService inner) : IAuditService
    {
        public Task WriteAsync(AuditEventEntry entry, CancellationToken cancellationToken = default) =>
            entry.EntityType == AuditEntityTypes.CalendarEntry
                ? throw new InvalidOperationException("deliberate audit staging failure")
                : inner.WriteAsync(entry, cancellationToken);
    }
}
