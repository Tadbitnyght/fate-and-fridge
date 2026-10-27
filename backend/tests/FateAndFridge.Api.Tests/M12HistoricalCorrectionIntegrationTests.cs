using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.LiveSync;
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
/// HTTP-level verification of the M12-06 historical correction
/// (docs/M12_PLAN.md §1.9, §2.2, §4; docs/SECURITY.md §10): past-only,
/// Admin-only, the shared dangerous-action guard (reason + typed date
/// confirmation), the locked-date acknowledgement that leaves the lock state
/// alone, the reused per-type field rules, the refreshed frozen snapshot, the
/// atomic flagged history row plus <c>calendar.historical_correction</c> audit
/// row, live sync, and the other dates staying untouched. The clock is injected
/// so the household today is deterministic. Requires a Docker daemon.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class M12HistoricalCorrectionIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string OverviewPath = "/api/v1/statistics/overview";
    private const string HistoryPath = "/api/v1/statistics/history";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m12-historical-correction-test";
    private const string TestIp = "203.0.113.9";
    private const string Reason = "Corrected the record after the fact";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date, and comfortably
    // after the real host date so the injected clock never expires a session.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly DateOnly PastDate = new(2026, 11, 10);
    private static readonly DateOnly OtherPastDate = new(2026, 11, 8);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- The correction write -------------------------------------------

    [Fact]
    public async Task Past_date_is_corrected_end_to_end_with_reason_history_audit_and_live_sync()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        var liveSync = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(new TestTimeProvider(Now), liveSync);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendCorrectionAsync(
            session,
            PastDate,
            new Dictionary<string, object?>
            {
                ["entry_type"] = "recipe",
                ["recipe_id"] = lasagna.Id,
                ["notes"] = "Family favourite",
                ["diner_count"] = 6,
            },
            Reason,
            Format(PastDate));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var day = await ReadJsonAsync(response);
        day.GetProperty("date").GetString().Should().Be(Format(PastDate));
        day.GetProperty("is_historical").GetBoolean().Should().BeTrue();
        day.GetProperty("can_edit").GetBoolean().Should().BeFalse();
        var entry = day.GetProperty("entry");
        entry.GetProperty("entry_type").GetString().Should().Be("recipe");
        entry.GetProperty("display_text").GetString().Should().Be("Lasagna");
        entry.GetProperty("notes").GetString().Should().Be("Family favourite");
        entry.GetProperty("diner_count").GetInt32().Should().Be(6);
        entry.GetProperty("include_groceries").GetBoolean().Should().BeTrue();
        entry.GetProperty("recipe").GetProperty("id").GetInt64().Should().Be(lasagna.Id);

        // Exactly one flagged history row carrying the reason and the before/after.
        var history = await ReadHistoryAsync(PastDate);
        var row = history.Should().ContainSingle().Subject;
        row.IsHistoricalCorrection.Should().BeTrue();
        row.ChangeReason.Should().Be(Reason);
        row.ChangedByUserId.Should().Be(admin.Id);
        row.OperationId.Should().NotBeNull();
        Snapshot(row.BeforeJson!)["entry_type"]!.GetValue<string>().Should().Be("eat_out");
        Snapshot(row.AfterJson!)["entry_type"]!.GetValue<string>().Should().Be("recipe");
        Snapshot(row.AfterJson!)["recipe_id"]!.GetValue<long>().Should().Be(lasagna.Id);
        Snapshot(row.AfterJson!)["snapshot_recipe_name"]!.GetValue<string>().Should().Be("Lasagna");
        Snapshot(row.AfterJson!)["diner_count"]!.GetValue<int>().Should().Be(6);

        // Exactly one audit row with the reason, actor, ip, target, and snapshots.
        var audit = await ReadAuditRowsAsync(AuditEventTypes.CalendarHistoricalCorrection);
        var auditRow = audit.Should().ContainSingle().Subject;
        auditRow.EntityType.Should().Be(AuditEntityTypes.CalendarEntry);
        auditRow.EntityId.Should().Be(row.CalendarEntryId!.Value.ToString(CultureInfo.InvariantCulture));
        auditRow.Reason.Should().Be(Reason);
        auditRow.ActorUserId.Should().Be(admin.Id);
        auditRow.IpAddress.Should().Be(TestIp);
        Snapshot(auditRow.BeforeJson!)["entry_type"]!.GetValue<string>().Should().Be("eat_out");
        Snapshot(auditRow.AfterJson!)["entry_type"]!.GetValue<string>().Should().Be("recipe");
        Snapshot(auditRow.MetadataJson)["meal_date"]!.GetValue<string>().Should().Be(Format(PastDate));

        // Live sync reuses the existing coarse event (docs/M12_PLAN.md §1.13).
        liveSync.Events.Should().ContainSingle(item =>
            item.Type == LiveSyncEventType.CalendarUpdated && item.Entity == "calendar");

        // Statistics/meal history reflect the correction: the eat_out entry did
        // not count as a recipe occurrence, the corrected recipe entry does.
        var overview = await ReadJsonAsync(await session.Client.GetAsync(OverviewPath, Ct));
        var top = overview.GetProperty("most_eaten").EnumerateArray().Single();
        top.GetProperty("name").GetString().Should().Be("Lasagna");
        top.GetProperty("times_eaten").GetInt32().Should().Be(1);

        var historyPage = await ReadJsonAsync(await session.Client.GetAsync(HistoryPath, Ct));
        historyPage.GetProperty("days").EnumerateArray()
            .Select(item => item.GetProperty("date").GetString())
            .Should().Contain(Format(PastDate));
    }

    [Fact]
    public async Task Today_and_future_dates_are_not_correctable()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var fields = new Dictionary<string, object?> { ["entry_type"] = "eat_out" };

        foreach (var date in new[] { Today, Today.AddDays(1) })
        {
            var response = await SendCorrectionAsync(session, date, fields, Reason, Format(date));
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var error = await ReadErrorAsync(response);
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.CalendarDateNotPast);
        }
    }

    [Fact]
    public async Task Locked_past_date_needs_acknowledgement_and_stays_locked()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place", isLocked: true);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var fields = new Dictionary<string, object?> { ["entry_type"] = "fend_for_yourself" };

        var refused = await SendCorrectionAsync(session, PastDate, fields, Reason, Format(PastDate));
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(refused)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateLocked);
        (await ReadEntryAsync(PastDate))!.EntryType.Should().Be(CalendarEntryType.EatOut);

        var accepted = await SendCorrectionAsync(
            session,
            PastDate,
            fields,
            Reason,
            Format(PastDate),
            lockAcknowledged: true);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        var day = await ReadJsonAsync(accepted);
        day.GetProperty("entry").GetProperty("is_locked").GetBoolean()
            .Should().BeTrue("the correction does not change the lock state");
        day.GetProperty("entry").GetProperty("entry_type").GetString()
            .Should().Be("fend_for_yourself");

        // Exactly one history row: the refused attempt wrote nothing.
        (await ReadHistoryAsync(PastDate)).Should().ContainSingle();
    }

    [Fact]
    public async Task Guard_field_errors_are_returned_for_missing_reason_and_wrong_confirmation()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var fields = new Dictionary<string, object?> { ["entry_type"] = "fend_for_yourself" };

        // Missing reason wins over a correct confirmation.
        var noReason = await SendCorrectionAsync(session, PastDate, fields, reason: null, Format(PastDate));
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var noReasonError = await ReadErrorAsync(noReason);
        noReasonError.GetProperty("code").GetString().Should().Be(ErrorCodes.ReasonRequired);
        noReasonError.GetProperty("details").GetProperty("field_errors").TryGetProperty(
            DangerousActionGuard.ReasonField,
            out _).Should().BeTrue();

        // A blank reason is rejected too.
        var blankReason = await SendCorrectionAsync(session, PastDate, fields, "   ", Format(PastDate));
        (await ReadErrorAsync(blankReason)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ReasonRequired);

        // Over the 500-character cap.
        var tooLong = await SendCorrectionAsync(
            session,
            PastDate,
            fields,
            new string('x', DangerousActionGuard.MaxReasonLength + 1),
            Format(PastDate));
        (await ReadErrorAsync(tooLong)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ReasonTooLong);

        // The confirmation must equal the date being corrected.
        var wrongConfirmation = await SendCorrectionAsync(
            session,
            PastDate,
            fields,
            Reason,
            Format(OtherPastDate));
        wrongConfirmation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var wrongError = await ReadErrorAsync(wrongConfirmation);
        wrongError.GetProperty("code").GetString().Should().Be(ErrorCodes.ConfirmationMismatch);
        wrongError.GetProperty("details").GetProperty("field_errors").TryGetProperty(
            DangerousActionGuard.ConfirmationField,
            out _).Should().BeTrue();

        // No rejected attempt may change the entry or write history/audit.
        (await ReadEntryAsync(PastDate))!.EntryType.Should().Be(CalendarEntryType.EatOut);
        (await ReadHistoryAsync(PastDate)).Should().BeEmpty();
        (await ReadAuditRowsAsync(AuditEventTypes.CalendarHistoricalCorrection)).Should().BeEmpty();
    }

    [Fact]
    public async Task Per_type_field_rules_of_the_normal_writes_still_apply()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(PastDate, CalendarEntryType.Vacation, null, "Vacation");
        await SeedEntryAsync(OtherPastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        // A vacation entry rejects a recipe id as a generic validation failure,
        // not as a missing-recipe error (BUG-004).
        var vacationWithRecipe = await SendCorrectionAsync(
            session,
            PastDate,
            new Dictionary<string, object?> { ["entry_type"] = "vacation", ["recipe_id"] = lasagna.Id },
            Reason,
            Format(PastDate));
        vacationWithRecipe.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var vacationError = await ReadErrorAsync(vacationWithRecipe);
        vacationError.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        vacationError.GetProperty("details").GetProperty("field_errors").TryGetProperty(
            "entry_type", out _).Should().BeTrue();

        // A recipe entry without a recipe id is rejected.
        var recipeWithoutId = await SendCorrectionAsync(
            session,
            OtherPastDate,
            new Dictionary<string, object?> { ["entry_type"] = "recipe" },
            Reason,
            Format(OtherPastDate));
        recipeWithoutId.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(recipeWithoutId)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarRecipeRequired);

        // An unknown entry type fails the shared validation.
        var unknownType = await SendCorrectionAsync(
            session,
            OtherPastDate,
            new Dictionary<string, object?> { ["entry_type"] = "not_a_type" },
            Reason,
            Format(OtherPastDate));
        unknownType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var unknownError = await ReadErrorAsync(unknownType);
        unknownError.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        unknownError.GetProperty("details").GetProperty("field_errors").TryGetProperty(
            "entry_type",
            out _).Should().BeTrue();

        // No rejected attempt changed either entry.
        (await ReadEntryAsync(PastDate))!.EntryType.Should().Be(CalendarEntryType.Vacation);
        (await ReadEntryAsync(OtherPastDate))!.EntryType.Should().Be(CalendarEntryType.EatOut);
        (await ReadHistoryAsync(PastDate)).Should().BeEmpty();
    }

    [Fact]
    public async Task Refreshed_snapshot_survives_a_rename_and_a_hard_delete()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var corrected = await SendCorrectionAsync(
            session,
            PastDate,
            new Dictionary<string, object?> { ["entry_type"] = "recipe", ["recipe_id"] = lasagna.Id },
            Reason,
            Format(PastDate));
        corrected.StatusCode.Should().Be(HttpStatusCode.OK);

        // The frozen snapshot was refreshed from the recipe at correction time.
        var stored = await ReadEntryAsync(PastDate);
        stored!.SnapshotRecipeName.Should().Be("Lasagna");
        stored.SnapshotDisplayText.Should().Be("Lasagna");

        // A later rename must not change the frozen historical display.
        var rename = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            $"/api/v1/recipes/{lasagna.Id}",
            """{"name":"Lasagna Deluxe"}""",
            session.CsrfToken);
        rename.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterRename = await ReadJsonAsync(await session.Client.GetAsync($"{CalendarPath}/{Format(PastDate)}", Ct));
        afterRename.GetProperty("entry").GetProperty("display_text").GetString().Should().Be("Lasagna");

        // A hard delete (raw SQL, as M12-08 will) leaves the history readable:
        // the entry is a tombstone and the snapshot still renders.
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM recipes WHERE id = {lasagna.Id}",
                Ct);
        }

        var tombstoneDay = await ReadJsonAsync(await session.Client.GetAsync($"{CalendarPath}/{Format(PastDate)}", Ct));
        var tombstone = tombstoneDay.GetProperty("entry");
        tombstone.GetProperty("recipe").ValueKind.Should().Be(JsonValueKind.Null);
        tombstone.GetProperty("entry_type").GetString().Should().Be("recipe");
        tombstone.GetProperty("display_text").GetString().Should().Be("Lasagna");

        var historyPage = await ReadJsonAsync(await session.Client.GetAsync(HistoryPath, Ct));
        historyPage.GetProperty("days").EnumerateArray()
            .Single(item => item.GetProperty("date").GetString() == Format(PastDate))
            .GetProperty("name").GetString().Should().Be("Lasagna");

        var overview = await ReadJsonAsync(await session.Client.GetAsync(OverviewPath, Ct));
        var ranking = overview.GetProperty("most_eaten").EnumerateArray().Single();
        ranking.GetProperty("name").GetString().Should().Be("Lasagna");
        ranking.GetProperty("status").GetString().Should().Be("deleted");
        ranking.GetProperty("times_eaten").GetInt32().Should().Be(1);

        // The correction history row's after-image still carries the snapshot.
        var history = await ReadHistoryAsync(PastDate);
        Snapshot(history.Should().ContainSingle().Subject.AfterJson!)["snapshot_recipe_name"]!
            .GetValue<string>().Should().Be("Lasagna");
    }

    [Fact]
    public async Task Other_dates_are_untouched()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        var untouched = await SeedEntryAsync(OtherPastDate, CalendarEntryType.Misc, null, "Potluck");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendCorrectionAsync(
            session,
            PastDate,
            new Dictionary<string, object?> { ["entry_type"] = "fend_for_yourself" },
            Reason,
            Format(PastDate));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var other = await ReadEntryAsync(OtherPastDate);
        other!.EntryType.Should().Be(CalendarEntryType.Misc);
        other.SnapshotDisplayText.Should().Be("Potluck");
        other.RecipeId.Should().BeNull();
        other.UpdatedAt.Should().Be(untouched.UpdatedAt);
        (await ReadHistoryAsync(OtherPastDate)).Should().BeEmpty();
        (await ReadHistoryAsync(PastDate)).Should().ContainSingle();
    }

    [Fact]
    public async Task History_and_audit_commit_atomically_and_a_forced_audit_failure_rolls_back()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var ok = await SendCorrectionAsync(
            session,
            PastDate,
            new Dictionary<string, object?> { ["entry_type"] = "recipe", ["recipe_id"] = lasagna.Id },
            Reason,
            Format(PastDate));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // Exactly one history row and one audit row commit with the change.
        (await ReadHistoryAsync(PastDate)).Should().ContainSingle();
        (await ReadAuditRowsAsync(AuditEventTypes.CalendarHistoricalCorrection)).Should().ContainSingle();

        // The second date keeps its original entry, so the rolled-back
        // correction's effect is observable.
        await SeedEntryAsync(OtherPastDate, CalendarEntryType.EatOut, null, "Pizza Place");

        // A forced audit staging failure rolls the whole correction back.
        using var failingFactory = CreateFactory(
            new TestTimeProvider(Now),
            liveSync: null,
            configureServices: services =>
            {
                services.RemoveAll<IAuditService>();
                services.AddScoped<IAuditService>(provider => new FailingCalendarAuditService(
                    new AuditService(
                        provider.GetRequiredService<AppDbContext>(),
                        provider.GetRequiredService<TimeProvider>(),
                        NullLogger<AuditService>.Instance)));
            });
        using var failingSession = await SignInAsync(failingFactory, "root", AdminPassword);

        var failed = await SendCorrectionAsync(
            failingSession,
            OtherPastDate,
            new Dictionary<string, object?> { ["entry_type"] = "vacation" },
            Reason,
            Format(OtherPastDate));
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await using (var context = fixture.CreateContext())
        {
            (await context.CalendarEntries.CountAsync(entry => entry.MealDate == OtherPastDate, Ct))
                .Should().Be(1, "the seeded entry is unchanged by the rolled-back correction");
            (await context.CalendarEntries.SingleAsync(entry => entry.MealDate == OtherPastDate, Ct))
                .EntryType.Should().Be(CalendarEntryType.EatOut);
        }

        (await ReadHistoryAsync(OtherPastDate)).Should().BeEmpty();
        (await ReadAuditRowsAsync(AuditEventTypes.CalendarHistoricalCorrection)).Should().ContainSingle();
    }

    [Fact]
    public async Task Correction_is_admin_only()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var member = await SeedUserAsync("member", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(member.Id, PermissionKeys.CalendarView);
        await MarkSetupCompletedAsync();
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        using var factory = CreateFactory(new TestTimeProvider(Now));

        var fields = new Dictionary<string, object?> { ["entry_type"] = "fend_for_yourself" };

        // Anonymous → 401 (no CSRF on an unauthenticated request).
        using (var anonymous = factory.CreateClient())
        {
            var response = await SendJsonAsync(
                anonymous,
                HttpMethod.Post,
                $"{CalendarPath}/{Format(PastDate)}/correction",
                CorrectionJson(fields, Reason, Format(PastDate)),
                csrfToken: null);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // A member with calendar.view → 403.
        using (var memberSession = await SignInAsync(factory, "member", MemberPassword))
        {
            var response = await SendCorrectionAsync(memberSession, PastDate, fields, Reason, Format(PastDate));
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // Admin → 200.
        using (var adminSession = await SignInAsync(factory, "root", AdminPassword))
        {
            var response = await SendCorrectionAsync(adminSession, PastDate, fields, Reason, Format(PastDate));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The single-user system actor is an Admin.
        await fixture.ResetAsync(Ct);
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        using var singleUserFactory = CreateFactory(new TestTimeProvider(Now));
        using (var client = singleUserFactory.CreateClient())
        {
            var response = await SendJsonAsync(
                client,
                HttpMethod.Post,
                $"{CalendarPath}/{Format(PastDate)}/correction",
                CorrectionJson(fields, Reason, Format(PastDate)),
                csrfToken: null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_correction_racing_a_batch_apply_does_not_500()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Chili");
        await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut, null, "Pizza Place");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var previewSession = await SignInAsync(factory, "root", AdminPassword);

        var preview = await SendJsonAsync(
            previewSession.Client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            JsonSerializer.Serialize(new { start_date = Format(Today), mode = "seven_days" }),
            previewSession.CsrfToken);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await ReadJsonAsync(preview)).GetProperty("draft").GetProperty("token").GetString()!;

        using var applySession = await SignInAsync(factory, "root", AdminPassword);
        using var correctionSession = await SignInAsync(factory, "root", AdminPassword);

        var applyTask = SendJsonAsync(
            applySession.Client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/apply",
            JsonSerializer.Serialize(new { token }),
            applySession.CsrfToken);
        var correctionTask = SendCorrectionAsync(
            correctionSession,
            PastDate,
            new Dictionary<string, object?> { ["entry_type"] = "recipe", ["recipe_id"] = lasagna.Id },
            Reason,
            Format(PastDate));

        var responses = await Task.WhenAll(applyTask, correctionTask);

        foreach (var response in responses)
        {
            ((int)response.StatusCode).Should().BeLessThan(
                500,
                "a correction racing a batch apply must never 500");
        }

        // The correction itself committed: the past date now carries a recipe.
        (await ReadEntryAsync(PastDate))!.EntryType.Should().Be(CalendarEntryType.Recipe);
        (await ReadHistoryAsync(PastDate)).Should().ContainSingle();
    }

    // ---- Helpers ---------------------------------------------------------

    private ApiFactory CreateFactory(
        TestTimeProvider clock,
        RecordingLiveSyncPublisher? liveSync = null,
        Action<IServiceCollection>? configureServices = null) =>
        fixture.CreateFactory(
            liveSyncPublisher: liveSync,
            configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
                configureServices?.Invoke(services);
            });

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonNode Snapshot(string? json) => JsonNode.Parse(json ?? "{}")!;

    private static string CorrectionJson(
        Dictionary<string, object?> fields,
        string? reason,
        string? confirmation,
        bool? lockAcknowledged = null)
    {
        var payload = new Dictionary<string, object?>(fields, StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["confirmation"] = confirmation,
        };
        if (lockAcknowledged is not null)
        {
            payload["lock_acknowledged"] = lockAcknowledged.Value;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static async Task<HttpResponseMessage> SendCorrectionAsync(
        SignedInClient session,
        DateOnly date,
        Dictionary<string, object?> fields,
        string? reason,
        string? confirmation,
        bool? lockAcknowledged = null) =>
        await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{Format(date)}/correction",
            CorrectionJson(fields, reason, confirmation, lockAcknowledged),
            session.CsrfToken);

    private async Task<List<CalendarEntryHistory>> ReadHistoryAsync(DateOnly date)
    {
        await using var context = fixture.CreateContext();
        return await context.CalendarEntryHistory
            .AsNoTracking()
            .Where(row => row.MealDate == date)
            .OrderBy(row => row.Id)
            .ToListAsync(Ct);
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(string eventType)
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents
            .AsNoTracking()
            .Where(row => row.EventType == eventType)
            .OrderBy(row => row.Id)
            .ToListAsync(Ct);
    }

    private async Task<CalendarEntry?> ReadEntryAsync(DateOnly date)
    {
        await using var context = fixture.CreateContext();
        return await context.CalendarEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.MealDate == date, Ct);
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
            HouseholdName = "M12 Correction Household",
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

    private async Task SeedPermissionAsync(long userId, string key)
    {
        await using var context = fixture.CreateContext();
        context.UserPermissions.Add(new UserPermission { UserId = userId, PermissionKey = key });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<Recipe> SeedRecipeAsync(string name)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            Status = RecipeStatus.Normal,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(Ct);
        return recipe;
    }

    private async Task<CalendarEntry> SeedEntryAsync(
        DateOnly date,
        CalendarEntryType type,
        long? recipeId,
        string displayText,
        bool isLocked = false,
        string? snapshotRecipeName = null)
    {
        await using var context = fixture.CreateContext();
        var entry = new CalendarEntry
        {
            MealDate = date,
            EntryType = type,
            RecipeId = recipeId,
            SnapshotRecipeName = snapshotRecipeName,
            SnapshotDisplayText = displayText,
            IsLocked = isLocked,
            IncludeGroceries = type == CalendarEntryType.Recipe,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.CalendarEntries.Add(entry);
        await context.SaveChangesAsync(Ct);
        return entry;
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

        return new SignedInClient(
            client,
            login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
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

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error");

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }

    /// <summary>
    /// Fails only the calendar audit staging, so the orchestration still logs in
    /// and the test observes exactly the correction transaction rolling back.
    /// </summary>
    private sealed class FailingCalendarAuditService(IAuditService inner) : IAuditService
    {
        public Task WriteAsync(AuditEventEntry entry, CancellationToken cancellationToken = default) =>
            entry.EntityType == AuditEntityTypes.CalendarEntry
                ? throw new InvalidOperationException("deliberate audit staging failure")
                : inner.WriteAsync(entry, cancellationToken);
    }
}
