using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FateAndFridge.Api.Auth;
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
/// HTTP-level verification of the M12-08 hard recipe delete (docs/M12_PLAN.md
/// §1.10, §2.2, §4; docs/DATABASE.md §14; docs/SECURITY.md §4): the snapshot
/// backfill, the tombstoning of past entries with <c>include_groceries</c>
/// preserved (the M11 guarantee), the removal of today/future references, the
/// image row + file removal, the child cascade with no broken foreign keys, the
/// single <c>recipe.hard_deleted</c> audit row with the before-image and counts,
/// atomicity, live sync, and Admin-only enforcement with no new permission key.
/// The clock is injected so the household today is deterministic. Requires a
/// Docker daemon.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class M12RecipeHardDeleteIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string RecipesPath = "/api/v1/recipes";
    private const string CalendarPath = "/api/v1/calendar";
    private const string OverviewPath = "/api/v1/statistics/overview";
    private const string HistoryPath = "/api/v1/statistics/history";
    private const string GroceriesPath = "/api/v1/groceries";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m12-hard-delete-test";
    private const string TestIp = "203.0.113.11";
    private const string Reason = "Duplicate recipe created by mistake";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date, and comfortably
    // after the real host date so the injected clock never expires a session.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly DateOnly PastDate = new(2026, 11, 10);
    private static readonly DateOnly OlderPastDate = new(2026, 11, 8);
    private static readonly DateOnly FutureDate = new(2026, 11, 20);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "faf-hard-delete-" + Guid.NewGuid().ToString("N"));

    private string ImagesRoot => Path.Combine(_dataDir, "images", "recipes");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    // ---- The tombstone guarantee (past meals survive) -------------------

    [Fact]
    public async Task Past_meals_survive_the_hard_delete_with_the_snapshot_and_statistics_intact()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedIngredientLinkAsync(lasagna.Id, "HardDelete Stats Ingredient");
        await SeedProteinLinkAsync(lasagna.Id, "HardDelete Stats Protein");
        await SeedEntryAsync(PastDate, CalendarEntryType.Recipe, lasagna.Id, "Lasagna");
        await SeedEntryAsync(OlderPastDate, CalendarEntryType.Recipe, lasagna.Id, "Lasagna");

        var liveSync = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(new TestTimeProvider(Now), liveSync);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var before = await ReadJsonAsync(await session.Client.GetAsync(OverviewPath, Ct));
        var beforeTop = before.GetProperty("most_eaten").EnumerateArray().Single();

        var response = await SendDeleteAsync(session, lasagna.Id, Reason, "Lasagna");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("recipe_id").GetInt64().Should().Be(lasagna.Id);
        body.GetProperty("removed_planned_entries").GetInt32().Should().Be(0);
        body.GetProperty("tombstoned_entries").GetInt32().Should().Be(2);
        body.GetProperty("removed_images").GetInt32().Should().Be(0);

        // Past entries survive as tombstones: recipe_id null, snapshots kept, and
        // include_groceries stays true (docs/M11_PLAN.md §1.5, §1.20).
        foreach (var date in new[] { PastDate, OlderPastDate })
        {
            var entry = await ReadEntryAsync(date);
            entry.Should().NotBeNull();
            entry!.RecipeId.Should().BeNull();
            entry.SnapshotRecipeName.Should().Be("Lasagna");
            entry.SnapshotDisplayText.Should().Be("Lasagna");
            entry.IncludeGroceries.Should().BeTrue();
        }

        // Statistics/meal-history counts do not drop; the tombstone renders as
        // "deleted" under the stored snapshot name (docs/M11_PLAN.md §1.5).
        var after = await ReadJsonAsync(await session.Client.GetAsync(OverviewPath, Ct));
        var afterTop = after.GetProperty("most_eaten").EnumerateArray().Single();
        afterTop.GetProperty("name").GetString().Should().Be("Lasagna");
        afterTop.GetProperty("status").GetString().Should().Be("deleted");
        afterTop.GetProperty("times_eaten").GetInt32().Should().Be(2);
        afterTop.GetProperty("times_eaten").GetInt32().Should().Be(beforeTop.GetProperty("times_eaten").GetInt32());
        after.GetProperty("meal_count_month").GetInt32()
            .Should().Be(before.GetProperty("meal_count_month").GetInt32());
        after.GetProperty("meal_count_year").GetInt32()
            .Should().Be(before.GetProperty("meal_count_year").GetInt32());
        after.GetProperty("never_eaten").GetInt32()
            .Should().Be(before.GetProperty("never_eaten").GetInt32());

        var historyPage = await ReadJsonAsync(await session.Client.GetAsync(HistoryPath, Ct));
        var pastDay = historyPage.GetProperty("days").EnumerateArray()
            .Single(item => item.GetProperty("date").GetString() == Format(PastDate));
        pastDay.GetProperty("name").GetString().Should().Be("Lasagna");
        pastDay.GetProperty("recipe_id").ValueKind.Should().Be(JsonValueKind.Null);

        // The recipe itself is gone: from the list and from the per-recipe read.
        var list = await ReadJsonAsync(await session.Client.GetAsync(RecipesPath, Ct));
        list.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetInt64())
            .Should().NotContain(lasagna.Id);
        var detail = await session.Client.GetAsync($"{RecipesPath}/{lasagna.Id}", Ct);
        detail.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(detail)).GetProperty("code").GetString().Should().Be(ErrorCodes.NotFound);

        // Live sync reuses the existing coarse events.
        liveSync.Events.Should().ContainSingle(item =>
            item.Type == LiveSyncEventType.RecipeUpdated && item.Entity == "recipes");
        liveSync.Events.Should().ContainSingle(item =>
            item.Type == LiveSyncEventType.CalendarUpdated && item.Entity == "calendar");
    }

    [Fact]
    public async Task Today_and_future_references_are_removed_and_the_recipe_leaves_groceries_and_the_randomizer()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var doomed = await SeedRecipeAsync("HardDelete Planned Recipe");
        await SeedIngredientLinkAsync(doomed.Id, "Zorg Spice");
        var survivor = await SeedRecipeAsync("HardDelete Survivor Recipe");
        await SeedEntryAsync(PastDate, CalendarEntryType.Recipe, doomed.Id, "HardDelete Planned Recipe");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, doomed.Id, "HardDelete Planned Recipe");
        await SeedEntryAsync(FutureDate, CalendarEntryType.Recipe, doomed.Id, "HardDelete Planned Recipe");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendDeleteAsync(session, doomed.Id, Reason, "HardDelete Planned Recipe");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("removed_planned_entries").GetInt32().Should().Be(2);
        body.GetProperty("tombstoned_entries").GetInt32().Should().Be(1);

        // Nothing is left planned against the deleted recipe.
        (await ReadEntryAsync(Today)).Should().BeNull();
        (await ReadEntryAsync(FutureDate)).Should().BeNull();
        var past = await ReadEntryAsync(PastDate);
        past!.RecipeId.Should().BeNull();
        past.IncludeGroceries.Should().BeTrue();

        // The grocery projection no longer carries the deleted recipe's
        // ingredient (the only contributing entry was today/future).
        var groceries = await ReadJsonAsync(
            await session.Client.GetAsync($"{GroceriesPath}?preset=next_7_days", Ct));
        var lines = groceries.GetProperty("departments").EnumerateArray()
            .SelectMany(department => department.GetProperty("items").EnumerateArray())
            .ToArray();
        lines.Select(line => line.GetProperty("name").GetString())
            .Should().NotContain("Zorg Spice");
        lines.SelectMany(line => line.GetProperty("sources").EnumerateArray())
            .Select(source => source.GetProperty("recipe_id").GetInt64())
            .Should().NotContain(doomed.Id);

        // The deleted recipe is no longer eligible for the randomizer: the only
        // candidate left is the survivor.
        var preview = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            JsonSerializer.Serialize(new { start_date = Format(Today), mode = "seven_days" }),
            session.CsrfToken);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewBody = await ReadJsonAsync(preview);
        var proposed = previewBody.GetProperty("days").EnumerateArray()
            .Select(day => day.GetProperty("proposed"))
            .Where(proposed => proposed.ValueKind == JsonValueKind.Object)
            .Select(proposed => proposed.GetProperty("recipe_id").GetInt64())
            .ToArray();
        proposed.Should().NotBeEmpty();
        proposed.Should().OnlyContain(id => id == survivor.Id);
        proposed.Should().NotContain(doomed.Id);
    }

    [Fact]
    public async Task Snapshots_are_backfilled_when_missing_and_a_stale_snapshot_still_renders()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var recipe = await SeedRecipeAsync("Backfill Lasagna");
        // A null/blank snapshot (unreachable through the M5 write path, reachable
        // through legacy/imported data) must be backfilled before the reference
        // is cleared.
        await SeedEntryAsync(
            OlderPastDate,
            CalendarEntryType.Recipe,
            recipe.Id,
            displayText: string.Empty,
            snapshotRecipeName: null);
        // A stale snapshot must stay frozen to the name it carried when eaten.
        await SeedEntryAsync(PastDate, CalendarEntryType.Recipe, recipe.Id, "Old Lasagna", snapshotRecipeName: "Old Lasagna");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendDeleteAsync(session, recipe.Id, Reason, "Backfill Lasagna");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var backfilled = await ReadEntryAsync(OlderPastDate);
        backfilled!.RecipeId.Should().BeNull();
        backfilled.SnapshotRecipeName.Should().Be("Backfill Lasagna");
        backfilled.SnapshotDisplayText.Should().Be("Backfill Lasagna");

        var frozen = await ReadEntryAsync(PastDate);
        frozen!.RecipeId.Should().BeNull();
        frozen.SnapshotRecipeName.Should().Be("Old Lasagna");
        frozen.SnapshotDisplayText.Should().Be("Old Lasagna");

        // Both tombstones render in the meal history.
        var historyPage = await ReadJsonAsync(await session.Client.GetAsync(HistoryPath, Ct));
        var days = historyPage.GetProperty("days").EnumerateArray().ToArray();
        days.Single(day => day.GetProperty("date").GetString() == Format(OlderPastDate))
            .GetProperty("name").GetString().Should().Be("Backfill Lasagna");
        days.Single(day => day.GetProperty("date").GetString() == Format(PastDate))
            .GetProperty("name").GetString().Should().Be("Old Lasagna");
    }

    // ---- Image rows and files -------------------------------------------

    [Fact]
    public async Task Image_row_and_file_are_removed()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var recipe = await SeedRecipeAsync("HardDelete Image Recipe");
        var imagePath = WriteImageFile("deleteme.png");
        await SeedImageAsync(recipe.Id, imagePath);
        File.Exists(imagePath).Should().BeTrue();

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendDeleteAsync(session, recipe.Id, Reason, "HardDelete Image Recipe");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("removed_images").GetInt32().Should().Be(1);

        await using (var context = fixture.CreateContext())
        {
            (await context.RecipeImages.CountAsync(image => image.RecipeId == recipe.Id, Ct)).Should().Be(0);
        }

        File.Exists(imagePath).Should().BeFalse("the image file is removed, not just the row");
    }

    // ---- Cascade and foreign keys ---------------------------------------

    [Fact]
    public async Task Child_rows_cascade_and_no_foreign_key_is_broken()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var recipe = await SeedRecipeWithChildrenAsync("HardDelete Cascade Recipe");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendDeleteAsync(session, recipe.Id, Reason, "HardDelete Cascade Recipe");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var context = fixture.CreateContext())
        {
            (await context.Recipes.CountAsync(candidate => candidate.Id == recipe.Id, Ct)).Should().Be(0);
            (await context.RecipeIngredientGroups.CountAsync(group => group.RecipeId == recipe.Id, Ct)).Should().Be(0);
            (await context.RecipeIngredients.CountAsync(ingredient => ingredient.RecipeId == recipe.Id, Ct)).Should().Be(0);
            (await context.RecipeInstructionSections.CountAsync(section => section.RecipeId == recipe.Id, Ct)).Should().Be(0);
            (await context.RecipeProteins.CountAsync(link => link.RecipeId == recipe.Id, Ct)).Should().Be(0);
            (await context.RecipeTags.CountAsync(link => link.RecipeId == recipe.Id, Ct)).Should().Be(0);
            (await context.RecipeImages.CountAsync(image => image.RecipeId == recipe.Id, Ct)).Should().Be(0);
        }

        // DB-level orphan check: no child row anywhere points at a missing recipe.
        (await CountOrphansAsync()).Should().Be(0);
    }

    // ---- Audit ----------------------------------------------------------

    [Fact]
    public async Task Audit_row_retains_the_before_image_and_counts_and_a_forced_failure_rolls_back()
    {
        await SeedAdminHouseholdAsync();
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var recipe = await SeedRecipeAsync("HardDelete Audited Recipe");
        await SeedIngredientLinkAsync(recipe.Id, "HardDelete Audit Ingredient");
        await SeedProteinLinkAsync(recipe.Id, "HardDelete Audit Protein");
        await SeedTagLinkAsync(recipe.Id, "HardDelete Audit Tag");
        await SeedEntryAsync(PastDate, CalendarEntryType.Recipe, recipe.Id, "HardDelete Audited Recipe");
        await SeedEntryAsync(FutureDate, CalendarEntryType.Recipe, recipe.Id, "HardDelete Audited Recipe");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendDeleteAsync(session, recipe.Id, Reason, "HardDelete Audited Recipe");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = await ReadAuditRowsAsync(AuditEventTypes.RecipeHardDeleted);
        var row = audit.Should().ContainSingle().Subject;
        row.EntityType.Should().Be(AuditEntityTypes.Recipe);
        row.EntityId.Should().Be(recipe.Id.ToString(CultureInfo.InvariantCulture));
        row.Reason.Should().Be(Reason);
        row.ActorUserId.Should().Be(admin.Id);
        row.IpAddress.Should().Be(TestIp);
        row.AfterJson.Should().BeNull("there is nothing after an irreversible delete");

        var before = Snapshot(row.BeforeJson!);
        before["id"]!.GetValue<long>().Should().Be(recipe.Id);
        before["name"]!.GetValue<string>().Should().Be("HardDelete Audited Recipe");
        before["status"]!.GetValue<string>().Should().Be("normal");
        before["is_favorite"]!.GetValue<bool>().Should().BeFalse();
        before["is_vegan"]!.GetValue<bool>().Should().BeFalse();
        before["is_vegetarian"]!.GetValue<bool>().Should().BeFalse();
        before["ingredient_count"]!.GetValue<int>().Should().Be(1);
        before["protein_count"]!.GetValue<int>().Should().Be(1);
        before["tag_count"]!.GetValue<int>().Should().Be(1);
        row.BeforeJson.Should().NotContain("storage_path", "image bytes/paths are never audited");

        var metadata = Snapshot(row.MetadataJson);
        metadata["removed_planned_entries"]!.GetValue<int>().Should().Be(1);
        metadata["tombstoned_entries"]!.GetValue<int>().Should().Be(1);
        metadata["removed_images"]!.GetValue<int>().Should().Be(0);

        // A forced audit staging failure rolls the whole delete back.
        var rollbackRecipe = await SeedRecipeAsync("HardDelete Rollback Recipe");
        var rollbackImage = WriteImageFile("rollback.png");
        await SeedImageAsync(rollbackRecipe.Id, rollbackImage);
        await SeedEntryAsync(FutureDate, CalendarEntryType.Recipe, rollbackRecipe.Id, "HardDelete Rollback Recipe");

        using var failingFactory = CreateFactory(
            new TestTimeProvider(Now),
            liveSync: null,
            configureServices: services =>
            {
                services.RemoveAll<IAuditService>();
                services.AddScoped<IAuditService>(provider => new FailingHardDeleteAuditService(
                    new AuditService(
                        provider.GetRequiredService<AppDbContext>(),
                        provider.GetRequiredService<TimeProvider>(),
                        NullLogger<AuditService>.Instance)));
            });
        using var failingSession = await SignInAsync(failingFactory, "root", AdminPassword);

        var failed = await SendDeleteAsync(failingSession, rollbackRecipe.Id, Reason, "HardDelete Rollback Recipe");
        failed.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        await using (var context = fixture.CreateContext())
        {
            (await context.Recipes.CountAsync(candidate => candidate.Id == rollbackRecipe.Id, Ct))
                .Should().Be(1, "the rolled-back delete leaves the recipe in place");
            (await context.RecipeImages.CountAsync(image => image.RecipeId == rollbackRecipe.Id, Ct))
                .Should().Be(1);
            var entry = await context.CalendarEntries.SingleAsync(candidate => candidate.MealDate == FutureDate, Ct);
            entry.RecipeId.Should().Be(rollbackRecipe.Id);
        }

        File.Exists(rollbackImage).Should().BeTrue("a rolled-back delete never deletes the file");
        (await ReadAuditRowsAsync(AuditEventTypes.RecipeHardDeleted)).Should().ContainSingle();
    }

    // ---- Guard, not found, and permissions ------------------------------

    [Fact]
    public async Task Guard_and_not_found_errors_are_reported_and_nothing_is_deleted()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var recipe = await SeedRecipeAsync("HardDelete Guard Recipe");
        await SeedEntryAsync(PastDate, CalendarEntryType.Recipe, recipe.Id, "HardDelete Guard Recipe");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        // Unknown id → 404 NOT_FOUND.
        var unknown = await SendDeleteAsync(session, recipe.Id + 9999, Reason, "HardDelete Guard Recipe");
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(unknown)).GetProperty("code").GetString().Should().Be(ErrorCodes.NotFound);

        // A blank reason wins over the confirmation.
        var noReason = await SendDeleteAsync(session, recipe.Id, "   ", "HardDelete Guard Recipe");
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var noReasonError = await ReadErrorAsync(noReason);
        noReasonError.GetProperty("code").GetString().Should().Be(ErrorCodes.ReasonRequired);
        noReasonError.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty(DangerousActionGuard.ReasonField, out _).Should().BeTrue();

        // Over the 500-character cap.
        var tooLong = await SendDeleteAsync(
            session,
            recipe.Id,
            new string('x', DangerousActionGuard.MaxReasonLength + 1),
            "HardDelete Guard Recipe");
        (await ReadErrorAsync(tooLong)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ReasonTooLong);

        // The confirmation must equal the exact recipe name.
        var wrongConfirmation = await SendDeleteAsync(session, recipe.Id, Reason, "harddelete guard recipe");
        wrongConfirmation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var wrongError = await ReadErrorAsync(wrongConfirmation);
        wrongError.GetProperty("code").GetString().Should().Be(ErrorCodes.ConfirmationMismatch);
        wrongError.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty(DangerousActionGuard.ConfirmationField, out _).Should().BeTrue();

        // No rejected attempt changed anything.
        (await ReadRecipeAsync(recipe.Id)).Should().NotBeNull();
        (await ReadEntryAsync(PastDate))!.RecipeId.Should().Be(recipe.Id);
        (await ReadAuditRowsAsync(AuditEventTypes.RecipeHardDeleted)).Should().BeEmpty();
    }

    [Fact]
    public async Task Hard_delete_is_admin_only()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        var recipe = await SeedRecipeAsync("HardDelete Permissions Recipe");
        await SeedEntryAsync(PastDate, CalendarEntryType.Recipe, recipe.Id, "HardDelete Permissions Recipe");
        using var factory = CreateFactory(new TestTimeProvider(Now));

        // Anonymous → 401.
        using (var anonymous = factory.CreateClient())
        {
            var response = await SendJsonAsync(
                anonymous,
                HttpMethod.Delete,
                $"{RecipesPath}/{recipe.Id}",
                DeleteJson(Reason, "HardDelete Permissions Recipe"),
                csrfToken: null);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // A member → 403.
        using (var memberSession = await SignInAsync(factory, "member", MemberPassword))
        {
            var response = await SendDeleteAsync(
                memberSession,
                recipe.Id,
                Reason,
                "HardDelete Permissions Recipe");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        // Admin → 200.
        using (var adminSession = await SignInAsync(factory, "root", AdminPassword))
        {
            var response = await SendDeleteAsync(
                adminSession,
                recipe.Id,
                Reason,
                "HardDelete Permissions Recipe");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The single-user system actor is an Admin.
        await fixture.ResetAsync(Ct);
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
        var singleUserRecipe = await SeedRecipeAsync("HardDelete Single User Recipe");
        using var singleUserFactory = CreateFactory(new TestTimeProvider(Now));
        using (var client = singleUserFactory.CreateClient())
        {
            var response = await SendJsonAsync(
                client,
                HttpMethod.Delete,
                $"{RecipesPath}/{singleUserRecipe.Id}",
                DeleteJson(Reason, "HardDelete Single User Recipe"),
                csrfToken: null);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task A_recipe_with_no_history_and_no_image_still_deletes()
    {
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var recipe = await SeedRecipeAsync("HardDelete Bare Recipe");

        var liveSync = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(new TestTimeProvider(Now), liveSync);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendDeleteAsync(session, recipe.Id, Reason, "HardDelete Bare Recipe");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("removed_planned_entries").GetInt32().Should().Be(0);
        body.GetProperty("tombstoned_entries").GetInt32().Should().Be(0);
        body.GetProperty("removed_images").GetInt32().Should().Be(0);
        (await ReadRecipeAsync(recipe.Id)).Should().BeNull();
        liveSync.Events.Should().ContainSingle(item => item.Type == LiveSyncEventType.RecipeUpdated);
        liveSync.Events.Should().NotContain(item => item.Type == LiveSyncEventType.CalendarUpdated);
    }

    // ---- Helpers ---------------------------------------------------------

    private ApiFactory CreateFactory(
        TestTimeProvider clock,
        RecordingLiveSyncPublisher? liveSync = null,
        Action<IServiceCollection>? configureServices = null) =>
        fixture.CreateFactory(
            _dataDir,
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
                configureServices?.Invoke(services);
            },
            liveSync);

    private static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonNode Snapshot(string? json) => JsonNode.Parse(json ?? "{}")!;

    private static string DeleteJson(string? reason, string? confirmation) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = reason,
            ["confirmation"] = confirmation,
        });

    private static Task<HttpResponseMessage> SendDeleteAsync(
        SignedInClient session,
        long recipeId,
        string? reason,
        string? confirmation) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{RecipesPath}/{recipeId}",
            DeleteJson(reason, confirmation),
            session.CsrfToken);

    private async Task<List<AuditEvent>> ReadAuditRowsAsync(string eventType)
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents
            .AsNoTracking()
            .Where(row => row.EventType == eventType)
            .OrderBy(row => row.Id)
            .ToListAsync(Ct);
    }

    private async Task<Recipe?> ReadRecipeAsync(long id)
    {
        await using var context = fixture.CreateContext();
        return await context.Recipes.AsNoTracking().SingleOrDefaultAsync(recipe => recipe.Id == id, Ct);
    }

    private async Task<CalendarEntry?> ReadEntryAsync(DateOnly date)
    {
        await using var context = fixture.CreateContext();
        return await context.CalendarEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.MealDate == date, Ct);
    }

    /// <summary>
    /// DB-level foreign-key check: the number of child rows (recipe children and
    /// calendar entries) whose recipe reference points at a missing recipe.
    /// </summary>
    private async Task<int> CountOrphansAsync()
    {
        await using var context = fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT count(*) FROM recipe_ingredients ri WHERE NOT EXISTS (SELECT 1 FROM recipes r WHERE r.id = ri.recipe_id))
            + (SELECT count(*) FROM recipe_ingredient_groups g WHERE NOT EXISTS (SELECT 1 FROM recipes r WHERE r.id = g.recipe_id))
            + (SELECT count(*) FROM recipe_instruction_sections s WHERE NOT EXISTS (SELECT 1 FROM recipes r WHERE r.id = s.recipe_id))
            + (SELECT count(*) FROM recipe_instruction_steps st WHERE NOT EXISTS (SELECT 1 FROM recipe_instruction_sections s WHERE s.id = st.section_id))
            + (SELECT count(*) FROM recipe_proteins p WHERE NOT EXISTS (SELECT 1 FROM recipes r WHERE r.id = p.recipe_id))
            + (SELECT count(*) FROM recipe_tags t WHERE NOT EXISTS (SELECT 1 FROM recipes r WHERE r.id = t.recipe_id))
            + (SELECT count(*) FROM recipe_images i WHERE NOT EXISTS (SELECT 1 FROM recipes r WHERE r.id = i.recipe_id))
            + (SELECT count(*) FROM calendar_entries c WHERE c.recipe_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM recipes r WHERE r.id = c.recipe_id))
            """;
        var scalar = await command.ExecuteScalarAsync(Ct);
        return Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private string WriteImageFile(string fileName)
    {
        Directory.CreateDirectory(ImagesRoot);
        var path = Path.Combine(ImagesRoot, fileName);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
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
            HouseholdName = "M12 Hard Delete Household",
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

    private async Task<Ingredient> SeedIngredientLinkAsync(long recipeId, string ingredientName)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)
            ReferenceDataNormalizer.Normalize(ingredientName)!;
        await using var context = fixture.CreateContext();
        var ingredient = new Ingredient
        {
            CanonicalName = normalized.Display,
            NormalizedName = normalized.Normalized,
            IsActive = true,
        };
        context.Ingredients.Add(ingredient);
        await context.SaveChangesAsync(Ct);
        context.RecipeIngredients.Add(new RecipeIngredient
        {
            RecipeId = recipeId,
            IngredientId = ingredient.Id,
            SortOrder = 0,
        });
        await context.SaveChangesAsync(Ct);
        return ingredient;
    }

    private async Task SeedProteinLinkAsync(long recipeId, string proteinName)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(proteinName)!;
        await using var context = fixture.CreateContext();
        var protein = new Protein
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            IsActive = true,
        };
        context.Proteins.Add(protein);
        await context.SaveChangesAsync(Ct);
        context.RecipeProteins.Add(new RecipeProtein { RecipeId = recipeId, ProteinId = protein.Id });
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedTagLinkAsync(long recipeId, string tagName)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(tagName)!;
        await using var context = fixture.CreateContext();
        var tag = new Tag
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
        };
        context.Tags.Add(tag);
        await context.SaveChangesAsync(Ct);
        context.RecipeTags.Add(new RecipeTag { RecipeId = recipeId, TagId = tag.Id });
        await context.SaveChangesAsync(Ct);
    }

    private async Task<RecipeImage> SeedImageAsync(long recipeId, string storagePath)
    {
        await using var context = fixture.CreateContext();
        var image = new RecipeImage
        {
            RecipeId = recipeId,
            StoragePath = storagePath,
            AltText = "seeded",
            CreatedAt = SeedTimestamp,
        };
        context.RecipeImages.Add(image);
        await context.SaveChangesAsync(Ct);
        return image;
    }

    private async Task<Recipe> SeedRecipeWithChildrenAsync(string name)
    {
        var recipe = await SeedRecipeAsync(name);
        await using var context = fixture.CreateContext();
        var group = new RecipeIngredientGroup { RecipeId = recipe.Id, Title = "Main", SortOrder = 0 };
        context.RecipeIngredientGroups.Add(group);
        await context.SaveChangesAsync(Ct);

        var ingredient = new Ingredient
        {
            CanonicalName = "HardDelete Cascade Ingredient",
            NormalizedName = "harddelete cascade ingredient",
            IsActive = true,
        };
        context.Ingredients.Add(ingredient);
        await context.SaveChangesAsync(Ct);
        context.RecipeIngredients.Add(new RecipeIngredient
        {
            RecipeId = recipe.Id,
            GroupId = group.Id,
            IngredientId = ingredient.Id,
            SortOrder = 0,
        });

        var section = new RecipeInstructionSection { RecipeId = recipe.Id, Title = "Steps", SortOrder = 0 };
        context.RecipeInstructionSections.Add(section);
        await context.SaveChangesAsync(Ct);
        context.RecipeInstructionSteps.Add(new RecipeInstructionStep
        {
            SectionId = section.Id,
            Text = "Mix everything.",
            SortOrder = 0,
        });

        var protein = new Protein
        {
            Name = "HardDelete Cascade Protein",
            NormalizedName = "harddelete cascade protein",
            IsActive = true,
        };
        var tag = new Tag { Name = "HardDelete Cascade Tag", NormalizedName = "harddelete cascade tag" };
        context.Proteins.Add(protein);
        context.Tags.Add(tag);
        await context.SaveChangesAsync(Ct);
        context.RecipeProteins.Add(new RecipeProtein { RecipeId = recipe.Id, ProteinId = protein.Id });
        context.RecipeTags.Add(new RecipeTag { RecipeId = recipe.Id, TagId = tag.Id });
        await context.SaveChangesAsync(Ct);
        return recipe;
    }

    private async Task<CalendarEntry> SeedEntryAsync(
        DateOnly date,
        CalendarEntryType type,
        long? recipeId,
        string displayText,
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

    /// <summary>Fails only the hard-delete audit staging so the test observes the
    /// whole delete transaction rolling back.</summary>
    private sealed class FailingHardDeleteAuditService(IAuditService inner) : IAuditService
    {
        public Task WriteAsync(AuditEventEntry entry, CancellationToken cancellationToken = default) =>
            entry.EventType == AuditEventTypes.RecipeHardDeleted
                ? throw new InvalidOperationException("deliberate audit staging failure")
                : inner.WriteAsync(entry, cancellationToken);
    }
}
