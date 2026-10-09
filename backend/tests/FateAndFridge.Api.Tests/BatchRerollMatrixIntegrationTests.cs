using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Issue #98–#101 (M7-04…M7-07): the docs/M7_PLAN.md §4 battery for Fill Empty,
/// lock behavior in a batch, vacation blocking, and batch concurrency, driven at
/// the HTTP level against PostgreSQL. Each test maps to a §4 bullet:
/// <list type="bullet">
/// <item>Scope / Fill Empty: only blank dates are proposed and existing
/// entries are byte-for-byte unchanged (docs/RANDOMIZER.md §12).</item>
/// <item>Vacation/locks: a vacation date is skipped and never aborts the batch;
/// a locked date is preserved but still counted as occurrence context
/// (docs/RANDOMIZER.md §4).</item>
/// <item>Concurrency: two concurrent applies of one draft yield one success and
/// one clean rejection (never a 500); an apply racing a single-date reroll
/// serializes on the same per-date advisory lock; overlapping applies take
/// their locks in ascending order without deadlocking.</item>
/// </list>
/// Selection is made deterministic by the single-Normal-candidate technique:
/// when exactly one recipe has <c>status = Normal</c> the weighted draw must
/// return it, so proposed ids and the recorded score breakdown become stable
/// assertions. Helpers mirror the sibling batch tests (they are private there).
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class BatchRerollMatrixIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string PreviewPath = "/api/v1/calendar/reroll/preview";
    private const string ApplyPath = "/api/v1/calendar/reroll/apply";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "batch-reroll-matrix-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM calendar_reroll_drafts; DELETE FROM calendar_entry_history; DELETE FROM calendar_entries;",
                Ct);
        }

        await fixture.ResetAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---------------------------------------------------------------------
    // Fill Empty (#98 / M7-04)
    // ---------------------------------------------------------------------

    /// <summary>
    /// docs/M7_PLAN.md §1.3 / §4 and docs/RANDOMIZER.md §12: Fill Empty proposes
    /// blank dates only. Every existing entry keeps its own unchanged reason and
    /// is left byte-for-byte intact by the apply.
    /// </summary>
    [Fact]
    public async Task Fill_empty_proposes_only_blank_dates_and_preserves_existing_entries()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, dinerCount: 2, notes: "keep me");
        await SeedEntryAsync(Today.AddDays(1), CalendarEntryType.Holiday, recipeId: null);
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.EatOut, recipeId: null);
        await SeedEntryAsync(Today.AddDays(3), CalendarEntryType.FendForYourself, recipeId: null);
        await SeedEntryAsync(Today.AddDays(4), CalendarEntryType.Recipe, recipe.Id, isLocked: true);
        await SeedEntryAsync(Today.AddDays(5), CalendarEntryType.Vacation, recipeId: null);

        var preservedDates = new[]
        {
            Today, Today.AddDays(1), Today.AddDays(2), Today.AddDays(3), Today.AddDays(4), Today.AddDays(5),
        };
        var before = await SnapshotsAsync(preservedDates);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "fill_empty"));

        // The existing entries are shown unchanged with their own reason.
        AssertUnchanged(preview, Today, reason: null);
        AssertUnchanged(preview, Today.AddDays(1), "special");
        AssertUnchanged(preview, Today.AddDays(2), "existing_non_recipe");
        AssertUnchanged(preview, Today.AddDays(3), "existing_non_recipe");
        AssertUnchanged(preview, Today.AddDays(4), "locked");
        AssertUnchanged(preview, Today.AddDays(5), "vacation");

        // Every remaining date of the month is blank and proposed.
        var proposed = ProposedByDate(preview);
        proposed.Keys.Should().NotIntersectWith(preservedDates);
        proposed.Should().HaveCount(10, "November 21–30 are blank");
        proposed.Values.Should().OnlyContain(id => id == recipe.Id);

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("changed_count").GetInt32().Should().Be(proposed.Count);
        root.GetProperty("days").GetArrayLength().Should().Be(proposed.Count);

        // Existing entries survive the apply byte-for-byte.
        (await SnapshotsAsync(preservedDates)).Should().Equal(before);

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Should().HaveCount(preservedDates.Length + proposed.Count);
        foreach (var (date, recipeId) in proposed)
        {
            stored.Single(entry => entry.MealDate == date).RecipeId.Should().Be(recipeId);
        }

        var history = await context.CalendarEntryHistory.AsNoTracking().ToListAsync(Ct);
        history.Should().HaveCount(proposed.Count);
        foreach (var row in history)
        {
            using var metadata = JsonDocument.Parse(row.MetadataJson!);
            metadata.RootElement.GetProperty("kind").GetString().Should().Be("fill_empty");
            metadata.RootElement.GetProperty("mode").GetString().Should().Be("fill_empty");
        }
    }

    /// <summary>
    /// docs/M7_PLAN.md §4: a fully-occupied Fill Empty range previews with zero
    /// proposals and applies as a no-op (<c>changed_count = 0</c>) without error.
    /// </summary>
    [Fact]
    public async Task Fill_empty_on_a_fully_occupied_range_is_a_no_op()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        var lastOfMonth = new DateOnly(Today.Year, Today.Month, DateTime.DaysInMonth(Today.Year, Today.Month));
        await SeedEntryAsync(lastOfMonth, CalendarEntryType.Recipe, recipe.Id);
        var before = await SnapshotsAsync([lastOfMonth]);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, lastOfMonth, "fill_empty"));
        preview.GetProperty("days").GetArrayLength().Should().Be(1);
        ProposedByDate(preview).Should().BeEmpty("the single date is occupied");
        AssertUnchanged(preview, lastOfMonth, reason: null);

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("changed_count").GetInt32().Should().Be(0);
        root.GetProperty("days").GetArrayLength().Should().Be(0);
        Guid.TryParse(root.GetProperty("operation").GetProperty("id").GetString(), out var operationId)
            .Should().BeTrue();
        operationId.Should().NotBeEmpty();

        (await SnapshotsAsync([lastOfMonth])).Should().Equal(before);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntryHistory.CountAsync(Ct)).Should().Be(0);
        var draft = await context.CalendarRerollDrafts.AsNoTracking().SingleAsync(Ct);
        draft.AppliedAt.Should().NotBeNull("a no-op apply still consumes the draft");
    }

    // ---------------------------------------------------------------------
    // Lock behavior in a batch (#99 / M7-05)
    // ---------------------------------------------------------------------

    /// <summary>
    /// docs/M7_PLAN.md §1.3 / §4: a locked date is <c>unchanged</c>/<c>locked</c>
    /// and is never a write target.
    /// </summary>
    [Fact]
    public async Task Locked_date_is_unchanged_and_never_written()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(
            Today,
            CalendarEntryType.Recipe,
            recipe.Id,
            isLocked: true,
            dinerCount: 3,
            notes: "locked plan");
        var before = await SnapshotsAsync([Today]);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        AssertUnchanged(preview, Today, "locked");
        DayFor(preview, Today.AddDays(1)).GetProperty("status").GetString().Should().Be("proposed");

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("changed_count").GetInt32().Should().Be(6);
        (await SnapshotsAsync([Today])).Should().Equal(before);

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.AsNoTracking().ToListAsync(Ct);
        history.Should().NotContain(row => row.MealDate == Today);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §4 / docs/M7_PLAN.md §4: a locked occurrence is still a
    /// planned occurrence, so the single Normal recipe locked on the range start
    /// must contribute the exact-repeat penalty to the next proposed date.
    /// </summary>
    [Fact]
    public async Task Locked_occurrence_still_counts_as_scoring_context()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, isLocked: true);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        AssertUnchanged(preview, Today, "locked");
        var proposed = DayFor(preview, Today.AddDays(1));
        proposed.GetProperty("status").GetString().Should().Be("proposed");
        proposed.GetProperty("proposed").GetProperty("recipe_id").GetInt64()
            .Should().Be(recipe.Id, "the only Normal recipe wins every draw");

        var metadata = await DraftMetadataAsync(Today.AddDays(1));
        metadata.GetProperty("repeat_occurrences").GetInt32()
            .Should().Be(1, "the locked occurrence on the range start is still counted");
        metadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(0.10m);
    }

    /// <summary>
    /// docs/M7_PLAN.md §1.3 / §4: a locked special entry is preserved exactly;
    /// the lock takes precedence in the reported reason.
    /// </summary>
    [Fact]
    public async Task Locked_special_entry_is_preserved()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today, CalendarEntryType.Holiday, recipeId: null, isLocked: true);
        var before = await SnapshotsAsync([Today]);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        AssertUnchanged(preview, Today, "locked");
        DayFor(preview, Today).GetProperty("current").GetProperty("entry_type").GetString()
            .Should().Be("holiday");

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await SnapshotsAsync([Today])).Should().Equal(before);
    }

    // ---------------------------------------------------------------------
    // Vacation blocking (#100 / M7-06)
    // ---------------------------------------------------------------------

    /// <summary>
    /// docs/M7_PLAN.md §1.5 / §4: a vacation date inside the range is skipped and
    /// never written, and it does not abort the rest of the batch.
    /// </summary>
    [Fact]
    public async Task Vacation_date_is_skipped_without_aborting_the_batch()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.Vacation, recipeId: null);
        var before = await SnapshotsAsync([Today.AddDays(2)]);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        AssertUnchanged(preview, Today.AddDays(2), "vacation");
        var proposed = ProposedByDate(preview);
        proposed.Should().HaveCount(6, "the vacation date is the only non-blank day");
        proposed.Should().NotContainKey(Today.AddDays(2));

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a vacation date never aborts the batch");
        var root = await ReadJsonAsync(response);
        root.GetProperty("changed_count").GetInt32().Should().Be(6);
        root.GetProperty("days").EnumerateArray()
            .Select(ParseDate).Should().NotContain(Today.AddDays(2));

        (await SnapshotsAsync([Today.AddDays(2)])).Should().Equal(before);

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.AsNoTracking().ToListAsync(Ct);
        history.Should().HaveCount(6);
        history.Should().NotContain(row => row.MealDate == Today.AddDays(2));
    }

    // ---------------------------------------------------------------------
    // Batch concurrency (#101 / M7-07)
    // ---------------------------------------------------------------------

    /// <summary>
    /// docs/M7_PLAN.md §1.8 / §1.12 / §4: two concurrent applies of the same
    /// draft let exactly one win; the other loses cleanly with
    /// <c>BATCH_DRAFT_EXPIRED</c> or <c>BATCH_CONFLICT</c>, never a 500. The
    /// assertion holds whether or not the requests overlap.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_applies_of_the_same_draft_allow_exactly_one_success()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var proposed = ProposedByDate(preview);
        var token = TokenOf(preview);

        var responses = await Task.WhenAll(
            ApplyAsync(session, token),
            ApplyAsync(session, token));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Should().NotContain(response => (int)response.StatusCode >= 500);

        var failure = responses.Single(response => response.StatusCode != HttpStatusCode.OK);
        failure.StatusCode.Should().BeOneOf(HttpStatusCode.Gone, HttpStatusCode.Conflict);
        (await ReadErrorAsync(failure)).GetProperty("code").GetString()
            .Should().BeOneOf(ErrorCodes.BatchDraftExpired, ErrorCodes.BatchConflict);

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Select(entry => entry.MealDate).Should().OnlyHaveUniqueItems();
        stored.Should().HaveCount(proposed.Count);
        var history = await context.CalendarEntryHistory.AsNoTracking().ToListAsync(Ct);
        history.Should().HaveCount(proposed.Count);
        history.Select(row => row.BatchOperationId).Distinct().Should().ContainSingle();
    }

    /// <summary>
    /// docs/M7_PLAN.md §1.12 / §4: an apply racing a single-date reroll on one
    /// target date serializes on the shared per-date advisory lock; neither
    /// request 500s and the stored calendar stays consistent.
    /// </summary>
    [Fact]
    public async Task Apply_racing_a_single_date_reroll_serializes_without_error()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var proposed = ProposedByDate(preview);
        var token = TokenOf(preview);

        var responses = await Task.WhenAll(
            ApplyAsync(session, token),
            RerollAsync(session, Today));

        responses.Should().NotContain(response => (int)response.StatusCode >= 500);
        responses[1].StatusCode.Should().Be(HttpStatusCode.OK, "the reroll always succeeds");
        responses[0].StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Select(entry => entry.MealDate).Should().OnlyHaveUniqueItems();
        stored.Should().ContainSingle(entry => entry.MealDate == Today);
        stored.Should().HaveCount(
            responses[0].StatusCode == HttpStatusCode.OK ? proposed.Count : 1,
            "a conflicting apply rolls back completely");
    }

    /// <summary>
    /// docs/M7_PLAN.md §1.12 / §4: an apply racing a manual single-date edit
    /// (<c>PUT /calendar/{date}</c>) on one target date never 500s and leaves a
    /// consistent calendar. The target date already has an entry, so both
    /// operations update the same row rather than racing to insert it.
    /// </summary>
    [Fact]
    public async Task Apply_racing_a_single_date_put_never_500s_and_keeps_one_entry()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var proposed = ProposedByDate(preview);
        proposed.Should().ContainKey(Today, "the existing plain recipe is a target");

        var responses = await Task.WhenAll(
            ApplyAsync(session, TokenOf(preview)),
            PutAsync(session, Today, """{"entry_type":"eat_out"}"""));

        responses.Should().NotContain(response => (int)response.StatusCode >= 500);
        responses[0].StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
        responses[1].StatusCode.Should().Be(HttpStatusCode.OK, "a manual upsert is not conflict-checked");

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Select(entry => entry.MealDate).Should().OnlyHaveUniqueItems();
        stored.Should().ContainSingle(entry => entry.MealDate == Today);
        stored.Should().HaveCount(
            responses[0].StatusCode == HttpStatusCode.OK ? proposed.Count : 1,
            "a conflicting apply rolls back completely");
    }

    /// <summary>
    /// docs/M7_PLAN.md §1.12 / §4: two applies whose target ranges overlap take
    /// their per-date locks in ascending order, so neither deadlocks. The
    /// observable outcome is that both requests terminate without a 500 and one
    /// (the loser) reports a clean conflict.
    /// </summary>
    [Fact]
    public async Task Concurrent_applies_of_overlapping_drafts_do_not_deadlock()
    {
        await SeedHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var editor = await SeedUserAsync("editor", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(editor.Id, PermissionKeys.CalendarEdit);
        await MarkSetupCompletedAsync();
        await SeedRecipeAsync("Lasagna");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var adminSession = await SignInAsync(factory, "root", AdminPassword);
        using var editorSession = await SignInAsync(factory, "editor", MemberPassword);

        var adminToken = TokenOf(
            await ReadJsonAsync(await PreviewAsync(adminSession, Today, "seven_days")));
        var editorToken = TokenOf(
            await ReadJsonAsync(await PreviewAsync(editorSession, Today.AddDays(3), "seven_days")));

        var responses = await Task.WhenAll(
            ApplyAsync(adminSession, adminToken),
            ApplyAsync(editorSession, editorToken));

        responses.Should().NotContain(response => (int)response.StatusCode >= 500);
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Single(response => response.StatusCode != HttpStatusCode.OK).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Select(entry => entry.MealDate).Should().OnlyHaveUniqueItems();
        stored.Should().HaveCount(7, "one seven-day draft commits and the other rolls back");
        (await context.CalendarEntryHistory.CountAsync(Ct)).Should().Be(7);
    }

    // ---------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------

    private static void AssertUnchanged(JsonElement root, DateOnly date, string? reason)
    {
        var day = DayFor(root, date);
        day.GetProperty("status").GetString().Should().Be("unchanged");
        day.GetProperty("unchanged_reason").GetString().Should().Be(reason);
        day.GetProperty("proposed").ValueKind.Should().Be(JsonValueKind.Null);
        day.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Object);
    }

    private static Dictionary<DateOnly, long> ProposedByDate(JsonElement root)
    {
        var result = new Dictionary<DateOnly, long>();
        foreach (var day in root.GetProperty("days").EnumerateArray())
        {
            if (day.GetProperty("status").GetString() != "proposed")
            {
                continue;
            }

            result[ParseDate(day)] = day.GetProperty("proposed").GetProperty("recipe_id").GetInt64();
        }

        return result;
    }

    private static JsonElement DayFor(JsonElement root, DateOnly date)
    {
        var expected = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return root.GetProperty("days").EnumerateArray()
            .Single(day => day.GetProperty("date").GetString() == expected);
    }

    private static JsonElement MetadataFor(JsonElement[] days, DateOnly date)
    {
        var expected = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return days.Single(day => day.GetProperty("date").GetString() == expected)
            .GetProperty("metadata");
    }

    private static string TokenOf(JsonElement root) =>
        root.GetProperty("draft").GetProperty("token").GetString()!;

    private static DateOnly ParseDate(JsonElement day) =>
        DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<JsonElement> DraftMetadataAsync(DateOnly date)
    {
        await using var context = fixture.CreateContext();
        var draft = await context.CalendarRerollDrafts.AsNoTracking().SingleAsync(Ct);
        using var document = JsonDocument.Parse(draft.ProposalJson);
        var days = document.RootElement.GetProperty("days").EnumerateArray().ToArray();
        return MetadataFor(days, date).Clone();
    }

    private async Task<string[]> SnapshotsAsync(DateOnly[] dates)
    {
        await using var context = fixture.CreateContext();
        var entries = await context.CalendarEntries.AsNoTracking()
            .Where(entry => dates.Contains(entry.MealDate))
            .ToListAsync(Ct);
        return dates
            .Select(date => Snapshot(entries.Single(entry => entry.MealDate == date)))
            .ToArray();
    }

    private static string Snapshot(CalendarEntry entry) =>
        string.Join(
            "|",
            entry.Id,
            entry.MealDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            entry.EntryType,
            entry.RecipeId?.ToString(CultureInfo.InvariantCulture) ?? "null",
            entry.IsLocked,
            entry.DinerCount?.ToString(CultureInfo.InvariantCulture) ?? "null",
            entry.RestaurantName ?? "null",
            entry.LocationText ?? "null",
            entry.CustomLabel ?? "null",
            entry.Notes ?? "null",
            entry.IconKey ?? "null",
            entry.IncludeGroceries,
            entry.SnapshotRecipeName ?? "null",
            entry.SnapshotDisplayText,
            entry.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private static Task<HttpResponseMessage> PreviewAsync(
        SignedInClient session,
        DateOnly start,
        string mode) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            PreviewPath,
            JsonSerializer.Serialize(new
            {
                start_date = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                mode,
            }),
            session.CsrfToken);

    private static Task<HttpResponseMessage> ApplyAsync(SignedInClient session, string token) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            ApplyPath,
            JsonSerializer.Serialize(new { token }),
            session.CsrfToken);

    private static Task<HttpResponseMessage> RerollAsync(SignedInClient session, DateOnly date) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date:yyyy-MM-dd}/reroll",
            json: null,
            session.CsrfToken);

    private static Task<HttpResponseMessage> PutAsync(
        SignedInClient session,
        DateOnly date,
        string json) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{date:yyyy-MM-dd}",
            json,
            session.CsrfToken);

    private async Task SeedAdminHouseholdAsync(int defaultDinerCount = 4)
    {
        await SeedHouseholdAsync(defaultDinerCount: defaultDinerCount);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
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

        var login = await client.SendAsync(request, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        return new SignedInClient(
            client,
            cookies,
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

        return await client.SendAsync(request, Ct);
    }

    private async Task SeedHouseholdAsync(
        string timezone = "America/Chicago",
        int defaultDinerCount = 4)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Batch Reroll Matrix Household",
            Timezone = timezone,
            DefaultDinerCount = defaultDinerCount,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.Multiuser,
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
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
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

    private async Task<Recipe> SeedRecipeAsync(
        string name,
        RecipeStatus status = RecipeStatus.Normal,
        bool isFavorite = false)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            Status = status,
            IsFavorite = isFavorite,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(Ct);
        return recipe;
    }

    private async Task SeedEntryAsync(
        DateOnly date,
        CalendarEntryType type,
        long? recipeId,
        bool isLocked = false,
        int? dinerCount = null,
        string? notes = null)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = type,
            RecipeId = recipeId,
            IsLocked = isLocked,
            DinerCount = dinerCount,
            Notes = notes,
            IncludeGroceries = type == CalendarEntryType.Recipe,
            SnapshotDisplayText = type.ToString(),
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error");

    private sealed class SignedInClient(HttpClient client, CookieContainer cookies, string csrfToken)
        : IDisposable
    {
        public HttpClient Client { get; } = client;

        public CookieContainer Cookies { get; } = cookies;

        public string CsrfToken { get; } = csrfToken;

        public void Dispose() => Client.Dispose();
    }
}
