using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M14-08 concurrency battery (docs/M14_PLAN.md §1.9, §4). Every interleaving is
/// forced deterministically with a <see cref="DbCommandInterceptor"/> that pauses
/// one request inside the exact write (or a two-party barrier that releases only
/// after both requests passed their pre-checks); no sleeps are used.
/// <para>
/// Closes <c>BUG-003</c> (single-date writer racing a batch apply on a blank date)
/// and <c>BUG-011</c> (calendar write racing an Admin recipe hard delete), then
/// covers the remaining §1.9 scenarios: two users on one date, grocery check
/// toggles, session revocation mid-request, the backup/restore operation gate and
/// maintenance 503s, and duplicate-submit races.
/// </para>
/// Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class M14ConcurrencyBatteryTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string PreviewPath = "/api/v1/calendar/reroll/preview";
    private const string ApplyPath = "/api/v1/calendar/reroll/apply";
    private const string RecipesPath = "/api/v1/recipes";
    private const string UsersPath = "/api/v1/users";
    private const string BackupsPath = "/api/v1/admin/backups";
    private const string StaplesPath = "/api/v1/groceries/staples";
    private const string ManualPath = "/api/v1/groceries/manual";
    private const string CheckPath = "/api/v1/groceries/check";
    private const string UncheckPath = "/api/v1/groceries/uncheck";
    private const string AdminPassword = "root horse battery staple";
    private const string SecondPassword = "second horse battery staple";
    private const string Reason = "M14-08 concurrency battery";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly DateOnly TargetDate = Today.AddDays(1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync(Ct);
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM calendar_reroll_drafts;", Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------
    // BUG-003 — single-date writes and batch apply share the date lock
    // ------------------------------------------------------------------

    /// <summary>
    /// The exact BUG-003 interleaving: the batch apply has taken its date locks
    /// and is paused right before its <c>calendar_entries</c> insert; a single
    /// <c>PUT</c> arrives for the same blank date. Before the fix the PUT had no
    /// lock, committed a second row, and the apply's insert raised 23505 → 500.
    /// </summary>
    [Fact]
    public async Task Batch_apply_paused_before_insert_vs_single_put_on_the_same_blank_date_never_500s()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Concurrency Lasagna");
        await SeedRecipeAsync("Concurrency Tacos");
        var manual = await SeedRecipeAsync("Concurrency Manual Override");

        var gate = new GatedDbCommandInterceptor("INSERT INTO calendar_entries");
        using var factory = CreateFactory(new TestTimeProvider(Now), gate);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(admin, Today, "seven_days"));
        var token = TokenOf(preview);
        var proposed = ProposedByDate(preview);
        var target = proposed.Keys.First(date => date >= Today);

        // The apply is paused inside its transaction, after the before-state
        // checks and after taking the per-date lock.
        var apply = ApplyAsync(admin, token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The single writer arrives for the same blank date.
        var single = PutAsync(admin, target, manual.Id);

        // Nothing is held by the test itself: release the apply immediately.
        gate.Release();

        var responses = await Task.WhenAll(apply, single);

        responses.Should().NotContain(response =>
            (int)response.StatusCode >= 500, "neither writer may 500");
        responses.Count(response => response.IsSuccessStatusCode).Should().Be(
            2, "the per-date advisory lock serializes the two writers");

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking()
            .Where(entry => entry.MealDate == target)
            .ToListAsync(Ct);
        stored.Should().ContainSingle("the unique meal_date row was never duplicated");
        stored[0].RecipeId.Should().Be(
            manual.Id, "the single PUT is the last writer and wins without a version token");

        var historyForTarget = await context.CalendarEntryHistory.AsNoTracking()
            .CountAsync(row => row.MealDate == target, Ct);
        historyForTarget.Should().Be(
            2, "one apply row plus one single-date row: no lost history");
    }

    /// <summary>
    /// The reverse order: the single <c>PUT</c> is paused at its own insert and
    /// the batch apply starts. Before the fix the apply committed first and the
    /// released PUT raised 23505 → 500; after the fix the PUT holds the date
    /// lock, so the apply sees the committed entry and returns the documented
    /// <c>BATCH_CONFLICT</c>.
    /// </summary>
    [Fact]
    public async Task Single_put_paused_before_insert_then_batch_apply_returns_batch_conflict()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Concurrency Lasagna");
        await SeedRecipeAsync("Concurrency Tacos");
        var manual = await SeedRecipeAsync("Concurrency Manual Override");

        var gate = new GatedDbCommandInterceptor("INSERT INTO calendar_entries");
        using var factory = CreateFactory(new TestTimeProvider(Now), gate);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(admin, Today, "seven_days"));
        var token = TokenOf(preview);

        // The single writer is paused after its blank-date read, before its insert.
        var single = PutAsync(admin, TargetDate, manual.Id);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var apply = ApplyAsync(admin, token);
        gate.Release();

        var singleResponse = await single;
        var applyResponse = await apply;

        singleResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the PUT holds the date lock and commits first");
        applyResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(applyResponse)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchConflict);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.AsNoTracking()
                .CountAsync(entry => entry.MealDate == TargetDate, Ct))
            .Should().Be(1);
        (await context.CalendarEntries.AsNoTracking()
                .SingleAsync(entry => entry.MealDate == TargetDate, Ct))
            .RecipeId.Should().Be(manual.Id);
    }

    /// <summary>
    /// The plain single-first order (no pause needed): a preview taken while the
    /// date was blank is stale by apply time, so the whole apply is refused with
    /// <c>BATCH_CONFLICT</c> and writes nothing.
    /// </summary>
    [Fact]
    public async Task Single_put_before_apply_refuses_the_whole_batch_with_batch_conflict()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Concurrency Lasagna");
        await SeedRecipeAsync("Concurrency Tacos");
        var manual = await SeedRecipeAsync("Concurrency Manual Override");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(admin, Today, "seven_days"));
        var proposed = ProposedByDate(preview);

        (await PutAsync(admin, TargetDate, manual.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var apply = await ApplyAsync(admin, TokenOf(preview));

        apply.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(apply)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchConflict);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.AsNoTracking().CountAsync(Ct)).Should().Be(1);
        (await context.CalendarEntryHistory.AsNoTracking()
                .CountAsync(row => row.BatchOperationId != null, Ct))
            .Should().Be(0, "a refused apply commits no batch history");
        proposed.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Batch_apply_before_a_single_put_keeps_one_row_and_last_write_wins()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Concurrency Lasagna");
        await SeedRecipeAsync("Concurrency Tacos");
        var manual = await SeedRecipeAsync("Concurrency Manual Override");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(admin, Today, "seven_days"));
        var proposed = ProposedByDate(preview);
        var target = proposed.Keys.First();

        (await ApplyAsync(admin, TokenOf(preview))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutAsync(admin, target, manual.Id)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking()
            .Where(entry => entry.MealDate == target)
            .ToListAsync(Ct);
        stored.Should().ContainSingle();
        stored[0].RecipeId.Should().Be(manual.Id);
        (await context.CalendarEntryHistory.AsNoTracking()
                .CountAsync(row => row.MealDate == target, Ct))
            .Should().Be(2);
    }

    // ------------------------------------------------------------------
    // BUG-011 — calendar writes racing an Admin recipe hard delete
    // ------------------------------------------------------------------

    /// <summary>
    /// The batch apply is paused at its <c>calendar_entries</c> insert. The
    /// recipe is then deleted underneath it (direct SQL, deliberately bypassing
    /// the cooperative advisory lock to reproduce the residual window). Before
    /// the fix the insert raised the recipe foreign-key violation → 500; after
    /// the fix the whole apply rolls back with <c>BATCH_CONFLICT</c> and no
    /// calendar, history, or consumed-draft rows remain.
    /// </summary>
    [Fact]
    public async Task Batch_apply_whose_recipe_is_deleted_in_the_insert_window_returns_conflict_and_rolls_back()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Concurrency Lasagna");
        await SeedRecipeAsync("Concurrency Tacos");

        var gate = new GatedDbCommandInterceptor("INSERT INTO calendar_entries");
        using var factory = CreateFactory(new TestTimeProvider(Now), gate);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(admin, Today, "seven_days"));
        var token = TokenOf(preview);
        var proposed = ProposedByDate(preview);
        var doomedRecipeId = proposed.Values.First();

        var apply = ApplyAsync(admin, token);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Simulate a hard delete that started in the residual window and did not
        // wait for the writer's recipe lock.
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM recipes WHERE id = {doomedRecipeId}", Ct);
        }

        gate.Release();
        var response = await apply;

        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict, "the FK race is a clean retryable conflict, never a 500");
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchConflict);

        await using var verify = fixture.CreateContext();
        (await verify.CalendarEntries.AsNoTracking().CountAsync(Ct)).Should().Be(
            0, "the whole apply transaction rolled back");
        (await verify.CalendarEntryHistory.AsNoTracking().CountAsync(Ct)).Should().Be(0);
        var draftToken = Guid.Parse(token);
        (await verify.CalendarRerollDrafts.AsNoTracking()
                .SingleAsync(draft => draft.Token == draftToken, Ct))
            .AppliedAt.Should().BeNull("a rolled-back apply must not consume the draft");
    }

    /// <summary>
    /// Same residual FK window for a single-date reroll: the candidate recipe is
    /// deleted after the reroll read it and before its insert commits.
    /// </summary>
    [Fact]
    public async Task Reroll_whose_recipe_is_deleted_in_the_insert_window_returns_conflict_and_rolls_back()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Concurrency Reroll Target");
        await SeedRecipeAsync("Concurrency Reroll Retired", RecipeStatus.Retired);

        var gate = new GatedDbCommandInterceptor("INSERT INTO calendar_entries");
        using var factory = CreateFactory(new TestTimeProvider(Now), gate);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var reroll = RerollAsync(admin, TargetDate);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM recipes WHERE id = {recipe.Id}", Ct);
        }

        gate.Release();
        var response = await reroll;

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchConflict);

        await using var verify = fixture.CreateContext();
        (await verify.CalendarEntries.AsNoTracking().CountAsync(Ct)).Should().Be(0);
        (await verify.CalendarEntryHistory.AsNoTracking().CountAsync(Ct)).Should().Be(0);
    }

    /// <summary>
    /// The cooperating form of the same race: a real Admin hard delete waits for
    /// an in-flight apply's per-recipe advisory lock, then reconciles the rows the
    /// apply committed. No foreign key is broken and both requests succeed.
    /// </summary>
    [Fact]
    public async Task Admin_hard_delete_waits_for_an_in_flight_apply_and_reconciles_the_new_rows()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Concurrency Lasagna");
        await SeedRecipeAsync("Concurrency Tacos");

        var gate = new GatedDbCommandInterceptor("INSERT INTO calendar_entries");
        using var factory = CreateFactory(new TestTimeProvider(Now), gate);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(admin, Today, "seven_days"));
        var proposed = ProposedByDate(preview);
        var doomedRecipeId = proposed.Values.First();

        var apply = ApplyAsync(admin, TokenOf(preview));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        string doomedName;
        await using (var nameContext = fixture.CreateContext())
        {
            doomedName = await nameContext.Recipes.AsNoTracking()
                .Where(recipe => recipe.Id == doomedRecipeId)
                .Select(recipe => recipe.Name)
                .SingleAsync(Ct);
        }

        using var second = await SignInAsync(factory, "root", AdminPassword);
        var delete = HardDeleteAsync(second, doomedRecipeId, doomedName);

        // The delete takes the same per-recipe lock and therefore cannot finish
        // while the apply is paused. Release the apply; then both complete.
        gate.Release();
        var applyResponse = await apply;
        var deleteResponse = await delete;

        applyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        (await context.Recipes.AsNoTracking().AnyAsync(recipe => recipe.Id == doomedRecipeId, Ct))
            .Should().BeFalse();
        (await context.CalendarEntries.AsNoTracking()
                .CountAsync(entry => entry.RecipeId == doomedRecipeId, Ct))
            .Should().Be(
                0, "the hard delete reconciled the rows the apply had just committed");
    }

    // ------------------------------------------------------------------
    // Scenario 1 — two users editing the same date
    // ------------------------------------------------------------------

    [Fact]
    public async Task Two_users_editing_one_blank_date_serialize_with_both_history_and_audit_rows()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("second", SecondPassword, UserRole.Admin);
        var firstRecipe = await SeedRecipeAsync("Concurrency Lasagna");
        var secondRecipe = await SeedRecipeAsync("Concurrency Tacos");

        var gate = new GatedDbCommandInterceptor("INSERT INTO calendar_entries");
        using var factory = CreateFactory(new TestTimeProvider(Now), gate);
        using var root = await SignInAsync(factory, "root", AdminPassword);
        using var other = await SignInAsync(factory, "second", SecondPassword);

        var first = PutAsync(root, TargetDate, firstRecipe.Id);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var second = PutAsync(other, TargetDate, secondRecipe.Id);
        gate.Release();

        var responses = await Task.WhenAll(first, second);
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking()
            .Where(entry => entry.MealDate == TargetDate)
            .ToListAsync(Ct);
        stored.Should().ContainSingle();
        stored[0].RecipeId.Should().Be(secondRecipe.Id, "the second request read the first entry");

        var entryId = stored[0].Id.ToString(CultureInfo.InvariantCulture);
        (await context.CalendarEntryHistory.AsNoTracking()
                .CountAsync(row => row.MealDate == TargetDate, Ct))
            .Should().Be(2, "create + update are both recorded");
        var auditActors = await context.AuditEvents.AsNoTracking()
            .Where(audit => audit.EntityType == AuditEntityTypes.CalendarEntry
                && audit.EntityId == entryId)
            .Select(audit => audit.ActorUserId)
            .ToListAsync(Ct);
        auditActors.Should().HaveCount(2, "no audit row is lost");
        auditActors.Should().OnlyHaveUniqueItems();
    }

    // ------------------------------------------------------------------
    // Scenario 3 — grocery check toggles
    // ------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_check_and_uncheck_of_one_identity_keep_one_row_and_never_500()
    {
        await SeedAdminHouseholdAsync();

        var identity = new string('a', 64);
        var barrier = new BarrierDbCommandInterceptor("INSERT INTO grocery_check_state");
        using var factory = CreateFactory(new TestTimeProvider(Now), barrier);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var check = SendJsonAsync(
            admin.Client, HttpMethod.Post, CheckPath, JsonSerializer.Serialize(new { identity }),
            admin.CsrfToken);
        var uncheck = SendJsonAsync(
            admin.Client, HttpMethod.Post, UncheckPath, JsonSerializer.Serialize(new { identity }),
            admin.CsrfToken);

        var responses = await Task.WhenAll(check, uncheck);
        responses.Should().NotContain(response => (int)response.StatusCode >= 500);
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var stored = await context.GroceryCheckStates.AsNoTracking()
            .Where(row => row.ItemIdentityHash == identity)
            .ToListAsync(Ct);
        stored.Should().ContainSingle("the unique identity index has one row");
        stored[0].Checked.Should().Be(
            stored[0].CheckedAt is not null,
            "checked state and its timestamp stay consistent after the lost-insert retry");
    }

    [Fact]
    public async Task Concurrent_checks_of_different_identities_and_users_keep_both_rows()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("second", SecondPassword, UserRole.Admin);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var root = await SignInAsync(factory, "root", AdminPassword);
        using var other = await SignInAsync(factory, "second", SecondPassword);

        var firstIdentity = new string('b', 64);
        var secondIdentity = new string('c', 64);

        var responses = await Task.WhenAll(
            SendJsonAsync(root.Client, HttpMethod.Post, CheckPath,
                JsonSerializer.Serialize(new { identity = firstIdentity }), root.CsrfToken),
            SendJsonAsync(other.Client, HttpMethod.Post, CheckPath,
                JsonSerializer.Serialize(new { identity = secondIdentity }), other.CsrfToken));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var rows = await context.GroceryCheckStates.AsNoTracking()
            .Where(row => row.ItemIdentityHash == firstIdentity || row.ItemIdentityHash == secondIdentity)
            .ToListAsync(Ct);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row => row.Checked);
    }

    // ------------------------------------------------------------------
    // Scenario 4 — session revocation mid-request
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_write_in_flight_during_session_revocation_completes_fully_and_the_next_request_is_401()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Concurrency Revoked Session");

        var gate = new GatedDbCommandInterceptor("INSERT INTO calendar_entries");
        using var factory = CreateFactory(new TestTimeProvider(Now), gate);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var write = PutAsync(admin, TargetDate, recipe.Id);
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                UPDATE user_sessions
                SET revoked_at = now(), revoked_reason = 'm14-08-revocation-test'
                WHERE revoked_at IS NULL
                """,
                Ct);
        }

        gate.Release();
        var response = await write;

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "authentication was decided at request start; the write completes");

        await using var verify = fixture.CreateContext();
        var entry = await verify.CalendarEntries.AsNoTracking()
            .SingleAsync(candidate => candidate.MealDate == TargetDate, Ct);
        (await verify.CalendarEntryHistory.AsNoTracking()
                .CountAsync(row => row.MealDate == TargetDate, Ct))
            .Should().Be(1, "the write is not partially applied");
        (await verify.AuditEvents.AsNoTracking()
                .CountAsync(audit => audit.EntityType == AuditEntityTypes.CalendarEntry
                    && audit.EntityId == entry.Id.ToString(CultureInfo.InvariantCulture), Ct))
            .Should().Be(1);

        var next = await GetAsync(admin.Client, $"{CalendarPath}/{TargetDate:yyyy-MM-dd}");
        next.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorAsync(next)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthRequired);
    }

    // ------------------------------------------------------------------
    // Scenario 5 — backup/restore operation gate and maintenance mode
    // ------------------------------------------------------------------

    [Fact]
    public async Task Backup_and_restore_are_refused_concurrently_and_writes_503_during_maintenance()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Concurrency Maintenance");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var gate = factory.Services.GetRequiredService<BackupOperationGate>();
        var restoreState = factory.Services.GetRequiredService<RestoreState>();
        var restores = factory.Services.GetRequiredService<RestoreService>();

        gate.TryEnter().Should().BeTrue("the test stands in for the in-flight operation");
        try
        {
            // A second restore is refused with the documented code before any
            // archive path is touched (the path below deliberately does not exist).
            var restore = await restores.BeginAsync(
                new RestoreSource.Upload(
                    Path.Combine(Path.GetTempPath(), "m14-08-does-not-exist.ffbackup"),
                    "m14-08-does-not-exist.ffbackup",
                    new string('0', 64)),
                Reason,
                actorUserId: null,
                ipAddress: null,
                "m14-08-restore",
                Ct);
            restore.Should().BeOfType<RestoreStartOutcome.Failure>()
                .Which.Code.Should().Be(ErrorCodes.RestoreInProgress);

            var backup = await SendJsonAsync(
                admin.Client, HttpMethod.Post, BackupsPath,
                """{"reason":"concurrent backup during a restore"}""", admin.CsrfToken);
            backup.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ReadErrorAsync(backup)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.BackupInProgress);

            restoreState.EnterMaintenance("m14-08-restore");
            try
            {
                var blocked = await PutAsync(admin, TargetDate, recipe.Id);
                blocked.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                (await ReadErrorAsync(blocked)).GetProperty("code").GetString()
                    .Should().Be(ErrorCodes.RestoreInProgress);

                (await GetAsync(admin.Client, $"{CalendarPath}/{TargetDate:yyyy-MM-dd}"))
                    .StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

                // Health stays reachable so the container check does not kill a
                // restoring process.
                (await GetAsync(admin.Client, "/api/v1/health")).StatusCode.Should()
                    .Be(HttpStatusCode.OK);

                await using var during = fixture.CreateContext();
                (await during.CalendarEntries.AsNoTracking().CountAsync(Ct)).Should().Be(
                    0, "no write reaches the database while maintenance holds");
            }
            finally
            {
                restoreState.ExitMaintenance();
            }

            // Writes resume with no partial/stale row left behind.
            (await PutAsync(admin, TargetDate, recipe.Id)).StatusCode.Should().Be(HttpStatusCode.OK);
            await using var after = fixture.CreateContext();
            (await after.CalendarEntries.AsNoTracking()
                    .CountAsync(entry => entry.MealDate == TargetDate, Ct))
                .Should().Be(1);
        }
        finally
        {
            gate.Exit();
        }

        restoreState.IsMaintenanceActive.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // Scenario 6 — duplicate-submit races
    // ------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_recipe_and_user_creates_serialize_to_one_row_and_one_clean_conflict()
    {
        await SeedAdminHouseholdAsync();
        var recipes = new BarrierDbCommandInterceptor("INSERT INTO recipes");
        var users = new BarrierDbCommandInterceptor("INSERT INTO users");

        using var factory = CreateFactory(new TestTimeProvider(Now), recipes, users);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var recipePayload = JsonSerializer.Serialize(new { name = "Concurrency Duplicate Recipe" });
        var recipeResponses = await Task.WhenAll(
            SendJsonAsync(admin.Client, HttpMethod.Post, RecipesPath, recipePayload, admin.CsrfToken),
            SendJsonAsync(admin.Client, HttpMethod.Post, RecipesPath, recipePayload, admin.CsrfToken));

        recipeResponses.Should().NotContain(response => (int)response.StatusCode >= 500);
        recipeResponses.Count(response => response.StatusCode == HttpStatusCode.Created)
            .Should().Be(1, "normalized recipe names are unique");
        var recipeConflict = recipeResponses.Single(response =>
            response.StatusCode != HttpStatusCode.Created);
        recipeConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(recipeConflict)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.RecipeExists);

        await using (var context = fixture.CreateContext())
        {
            (await context.Recipes.AsNoTracking()
                    .CountAsync(recipe => recipe.Name == "Concurrency Duplicate Recipe", Ct))
                .Should().Be(1);
        }

        var userPayload = """{"username":"m14race","role":"member"}""";
        var userResponses = await Task.WhenAll(
            SendJsonAsync(admin.Client, HttpMethod.Post, UsersPath, userPayload, admin.CsrfToken),
            SendJsonAsync(admin.Client, HttpMethod.Post, UsersPath, userPayload, admin.CsrfToken));

        userResponses.Should().NotContain(response => (int)response.StatusCode >= 500);
        userResponses.Count(response => response.StatusCode == HttpStatusCode.Created)
            .Should().Be(1, "normalized usernames are unique");
        userResponses.Single(response => response.StatusCode != HttpStatusCode.Created)
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using (var context = fixture.CreateContext())
        {
            (await context.Users.AsNoTracking()
                    .CountAsync(user => user.NormalizedUsername == "m14race", Ct))
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task Duplicate_staple_creates_match_the_documented_application_only_uniqueness()
    {
        await SeedAdminHouseholdAsync();
        var barrier = new BarrierDbCommandInterceptor("INSERT INTO grocery_staples");
        using var factory = CreateFactory(new TestTimeProvider(Now), barrier);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var payload = JsonSerializer.Serialize(new { name = "Concurrency Duplicate Staple" });
        var responses = await Task.WhenAll(
            SendJsonAsync(admin.Client, HttpMethod.Post, StaplesPath, payload, admin.CsrfToken),
            SendJsonAsync(admin.Client, HttpMethod.Post, StaplesPath, payload, admin.CsrfToken));

        // docs/DATABASE.md (M8-05 known limitation): unique *active* staple names
        // are application-enforced only, so a concurrent pair may leave two rows.
        // The battery's requirement is no 500, a clean 201/409 result, and fully
        // formed rows — BUG-016 records the duplicate as a minor.
        responses.Should().NotContain(response => (int)response.StatusCode >= 500);
        responses.Should().OnlyContain(response =>
            response.StatusCode == HttpStatusCode.Created
            || response.StatusCode == HttpStatusCode.Conflict);

        await using var context = fixture.CreateContext();
        var rows = await context.GroceryStaples.AsNoTracking()
            .Where(staple => staple.Name == "Concurrency Duplicate Staple")
            .ToListAsync(Ct);
        rows.Should().HaveCountGreaterThanOrEqualTo(1);
        rows.Should().OnlyContain(staple => staple.IsActive);
        rows.Select(staple => staple.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Duplicate_manual_item_creates_keep_both_rows_fully_formed_and_never_500()
    {
        await SeedAdminHouseholdAsync();
        var barrier = new BarrierDbCommandInterceptor("INSERT INTO manual_grocery_items");
        using var factory = CreateFactory(new TestTimeProvider(Now), barrier);
        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var payload = JsonSerializer.Serialize(new
        {
            name = "Concurrency Duplicate Manual",
            range_start = Today.ToString("yyyy-MM-dd"),
            range_days = 7,
        });

        var responses = await Task.WhenAll(
            SendJsonAsync(admin.Client, HttpMethod.Post, ManualPath, payload, admin.CsrfToken),
            SendJsonAsync(admin.Client, HttpMethod.Post, ManualPath, payload, admin.CsrfToken));

        // Manual items are free text by design and carry no unique key
        // (docs/M8_PLAN.md §1.8); both creates may succeed. The battery requires
        // no 500 and no partial/audit loss — BUG-015 records the duplicate-submit
        // UX as a minor.
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.Created);

        await using var context = fixture.CreateContext();
        var rows = await context.ManualGroceryItems.AsNoTracking()
            .Where(item => item.Name == "Concurrency Duplicate Manual")
            .ToListAsync(Ct);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(item => item.RangeDays == 7 && item.RangeStart == Today);
        (await context.AuditEvents.AsNoTracking()
                .CountAsync(audit => audit.EntityType == AuditEntityTypes.GroceryManualItem, Ct))
            .Should().Be(2, "every accepted create has its audit row");
    }

    // ------------------------------------------------------------------
    // Seeding and HTTP helpers
    // ------------------------------------------------------------------

    private ApiFactory CreateFactory(
        TestTimeProvider clock,
        params DbCommandInterceptor[] interceptors) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);

            if (interceptors.Length == 0)
            {
                return;
            }

            // Program registers AppDbContext against the fixture connection
            // string; rebuild it with the same options plus the gates so every
            // request-scoped context is observed.
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options
                .UseNpgsql(fixture.ConnectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(interceptors));
        });

    private async Task SeedAdminHouseholdAsync()
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M14 concurrency battery",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.Multiuser,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);

        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
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
            MustChangePassword = false,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private async Task<Recipe> SeedRecipeAsync(
        string name,
        RecipeStatus status = RecipeStatus.Normal)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = new Recipe
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            Status = status,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(Ct);
        return recipe;
    }

    private static Task<HttpResponseMessage> PutAsync(
        SignedInClient session,
        DateOnly date,
        long recipeId) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{date:yyyy-MM-dd}",
            JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipeId }),
            session.CsrfToken);

    private static Task<HttpResponseMessage> RerollAsync(SignedInClient session, DateOnly date) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date:yyyy-MM-dd}/reroll",
            json: null,
            session.CsrfToken);

    private static Task<HttpResponseMessage> PreviewAsync(
        SignedInClient session,
        DateOnly start,
        string mode) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            PreviewPath,
            JsonSerializer.Serialize(new { start_date = start.ToString("yyyy-MM-dd"), mode }),
            session.CsrfToken);

    private static Task<HttpResponseMessage> ApplyAsync(SignedInClient session, string token) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            ApplyPath,
            JsonSerializer.Serialize(new { token }),
            session.CsrfToken);

    private static Task<HttpResponseMessage> HardDeleteAsync(
        SignedInClient session,
        long recipeId,
        string confirmation) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{RecipesPath}/{recipeId}",
            JsonSerializer.Serialize(new { reason = Reason, confirmation }),
            session.CsrfToken);

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path) =>
        await client.GetAsync(path, Ct);

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

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
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

            var date = DateOnly.ParseExact(
                day.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            result[date] = day.GetProperty("proposed").GetProperty("recipe_id").GetInt64();
        }

        return result;
    }

    private static string TokenOf(JsonElement root) =>
        root.GetProperty("draft").GetProperty("token").GetString()!;

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

        var response = await client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var values)
            ? values.Single()
            : throw new InvalidOperationException("Login did not return an antiforgery token.");

        return new SignedInClient(client, token);
    }

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
