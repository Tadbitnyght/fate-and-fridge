using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.LiveSync;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M9-02…M9-05 live-sync emission matrix (docs/M9_PLAN.md §1.5, §1.14, §2.2).
/// Every household-calendar write path emits exactly one coarse event of the
/// right type after it commits, never on a failed/rolled-back/validation-error
/// request or a read, and lock toggles never double-emit <c>calendar.updated</c>.
/// The shared test host is bootstrapped with a resettable
/// <see cref="RecordingLiveSyncPublisher"/> (see <c>ApiFactory</c>) so the exact
/// envelope is asserted without a socket, plus a two-client SignalR broadcast
/// test and a completeness guard over every mutating route. Requires a Docker
/// daemon; run with <c>dotnet test --filter "Category!=Integration"</c> to skip
/// locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class M9LiveSyncEventMatrixTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string OperationsPath = "/api/v1/operations";
    private const string BatchOperationsPath = "/api/v1/batch-operations";
    private const string GroceriesPath = "/api/v1/groceries";
    private const string PreferencesPath = "/api/v1/groceries/preferences";
    private const string RecipesPath = "/api/v1/recipes";
    private const string ImportPath = "/api/v1/import/recipe-url";
    private const string ImportCheckPath = "/api/v1/import/check";
    private const string SettingsPath = "/api/v1/settings";
    private const string DepartmentsPath = "/api/v1/departments";
    private const string UnitsPath = "/api/v1/units";
    private const string IngredientsPath = "/api/v1/ingredients";
    private const string ProteinsPath = "/api/v1/proteins";
    private const string TagsPath = "/api/v1/tags";

    private const string AdminPassword = "root horse battery staple";
    private const string TestUserAgent = "m9-live-sync-matrix-test";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date, and comfortably
    // before the real host date's session expiry behaviour stays stable.
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ---- Calendar --------------------------------------------------------

    [Fact]
    public async Task Calendar_put_emits_calendar_updated_with_the_entry_id()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        var recipe = await SeedRecipeAsync("M9 Calendar Recipe");

        var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipe.Id }),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entryId = (await ReadJsonAsync(response)).GetProperty("entry").GetProperty("id").GetInt64();

        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEntity.Calendar,
            entryId.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Calendar_delete_emits_calendar_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var put = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"eat_out"}""",
            csrfToken: null);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Clear();

        var delete = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{CalendarPath}/{Format(Today)}",
            json: null,
            csrfToken: null);

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSingleEvent(recording, LiveSyncEventType.CalendarUpdated, LiveSyncEntity.Calendar);
    }

    [Fact]
    public async Task Calendar_reroll_emits_calendar_updated_with_the_operation_id()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        await SeedRecipeAsync("M9 Reroll One");
        await SeedRecipeAsync("M9 Reroll Two");

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/{Format(Today)}/reroll",
            json: null,
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var day = await ReadJsonAsync(response);
        var entryId = day.GetProperty("entry").GetProperty("id").GetInt64();
        var operationId = day.GetProperty("operation").GetProperty("id").GetString()!;

        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEntity.Calendar,
            entryId.ToString(CultureInfo.InvariantCulture),
            operationId);
    }

    [Fact]
    public async Task Calendar_batch_apply_emits_calendar_updated_with_the_operation_id()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        await SeedRecipeAsync("M9 Batch Apply One");
        await SeedRecipeAsync("M9 Batch Apply Two");

        var preview = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            """{"start_date":"2026-11-15","mode":"seven_days"}""",
            csrfToken: null);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await ReadJsonAsync(preview)).GetProperty("draft").GetProperty("token").GetString()!;
        recording.Clear();

        var apply = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/apply",
            JsonSerializer.Serialize(new { token }),
            csrfToken: null);
        apply.StatusCode.Should().Be(HttpStatusCode.OK);
        var batchId = (await ReadJsonAsync(apply)).GetProperty("operation").GetProperty("id").GetString()!;

        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEntity.Calendar,
            id: null,
            batchId);
    }

    [Fact]
    public async Task Operations_undo_emits_calendar_updated_with_the_operation_id()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        await SeedRecipeAsync("M9 Undo One");
        await SeedRecipeAsync("M9 Undo Two");

        var reroll = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/{Format(Today)}/reroll",
            json: null,
            csrfToken: null);
        reroll.StatusCode.Should().Be(HttpStatusCode.OK);
        var operationId = (await ReadJsonAsync(reroll))
            .GetProperty("operation").GetProperty("id").GetString()!;
        recording.Clear();

        var undo = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{OperationsPath}/{operationId}/undo",
            json: null,
            csrfToken: null);

        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEntity.Calendar,
            id: null,
            operationId);
    }

    [Fact]
    public async Task Batch_operations_undo_emits_calendar_updated_with_the_operation_id()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        await SeedRecipeAsync("M9 Batch Undo One");
        await SeedRecipeAsync("M9 Batch Undo Two");

        var preview = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            """{"start_date":"2026-11-15","mode":"seven_days"}""",
            csrfToken: null);
        var token = (await ReadJsonAsync(preview)).GetProperty("draft").GetProperty("token").GetString()!;
        var apply = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/apply",
            JsonSerializer.Serialize(new { token }),
            csrfToken: null);
        apply.StatusCode.Should().Be(HttpStatusCode.OK);
        var batchId = (await ReadJsonAsync(apply)).GetProperty("operation").GetProperty("id").GetString()!;
        recording.Clear();

        var undo = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{BatchOperationsPath}/{batchId}/undo",
            json: null,
            csrfToken: null);

        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEntity.Calendar,
            id: null,
            batchId);
    }

    [Fact]
    public async Task Calendar_lock_emits_only_calendar_lock_changed()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var put = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"vacation"}""",
            csrfToken: null);
        var entryId = (await ReadJsonAsync(put)).GetProperty("entry").GetProperty("id").GetInt64();
        recording.Clear();

        var lockResponse = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/{Format(Today)}/lock",
            json: null,
            csrfToken: null);

        lockResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarLockChanged,
            LiveSyncEntity.Calendar,
            entryId.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Calendar_unlock_emits_only_calendar_lock_changed()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"vacation"}""",
            csrfToken: null);
        await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/{Format(Today)}/lock",
            json: null,
            csrfToken: null);
        recording.Clear();

        var unlockResponse = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/{Format(Today)}/unlock",
            json: null,
            csrfToken: null);

        unlockResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var unlockDay = await ReadJsonAsync(unlockResponse);
        var entryId = unlockDay.GetProperty("entry").GetProperty("id").GetInt64();
        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarLockChanged,
            LiveSyncEntity.Calendar,
            entryId.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Calendar_batch_preview_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        await SeedRecipeAsync("M9 Preview One");
        await SeedRecipeAsync("M9 Preview Two");

        var preview = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            """{"start_date":"2026-11-15","mode":"seven_days"}""",
            csrfToken: null);

        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Events.Should().BeEmpty("a batch preview persists only a draft");
    }

    [Fact]
    public async Task Calendar_put_actor_is_the_session_user()
    {
        var admin = await SeedMultiuserAdminAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var recipe = await SeedRecipeAsync("M9 Actor Recipe");

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipe.Id }),
            session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entryId = (await ReadJsonAsync(response)).GetProperty("entry").GetProperty("id").GetInt64();
        AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEntity.Calendar,
            entryId.ToString(CultureInfo.InvariantCulture),
            operationId: null,
            actorUserId: admin.Id);
    }

    // ---- Groceries -------------------------------------------------------

    [Fact]
    public async Task Grocery_manual_create_emits_groceries_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/manual",
            ManualJson("M9 Manual Milk"),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Grocery_manual_update_emits_groceries_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/manual",
            ManualJson("M9 Manual Update"),
            csrfToken: null);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        recording.Clear();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{GroceriesPath}/manual/{id}",
            """{"name":"M9 Manual Updated"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Grocery_manual_delete_emits_groceries_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/manual",
            ManualJson("M9 Manual Delete"),
            csrfToken: null);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        recording.Clear();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{GroceriesPath}/manual/{id}",
            json: null,
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Grocery_staple_create_emits_groceries_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/staples",
            """{"name":"M9 Staple"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Grocery_staple_update_emits_groceries_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/staples",
            """{"name":"M9 Staple Update"}""",
            csrfToken: null);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        recording.Clear();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{GroceriesPath}/staples/{id}",
            """{"name":"M9 Staple Updated"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Grocery_staple_add_to_list_emits_groceries_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/staples",
            """{"name":"M9 Staple Add"}""",
            csrfToken: null);
        var stapleId = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        recording.Clear();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/staples/{stapleId}/add-to-list",
            $$"""{"range_start":"{{Format(Today)}}","range_days":7}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var itemId = (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            itemId.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Grocery_check_emits_groceries_updated_keyed_by_identity()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        var identity = new string('a', 64);

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/check",
            JsonSerializer.Serialize(new { identity }),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            identity);
    }

    [Fact]
    public async Task Grocery_uncheck_emits_groceries_updated_keyed_by_identity()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        var identity = new string('b', 64);

        var check = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/check",
            JsonSerializer.Serialize(new { identity }),
            csrfToken: null);
        check.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Clear();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/uncheck",
            JsonSerializer.Serialize(new { identity }),
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.GroceriesUpdated,
            LiveSyncEntity.Groceries,
            identity);
    }

    [Fact]
    public async Task Grocery_preferences_patch_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            PreferencesPath,
            """{"hide_checked":true}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Events.Should().BeEmpty("grocery preferences are per-user and private");
    }

    // ---- Recipes ---------------------------------------------------------

    [Fact]
    public async Task Recipe_create_emits_recipe_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            RecipesPath,
            """{"name":"M9 Created Recipe"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(response)).GetProperty("id").GetInt64();
        AssertSingleEvent(
            recording,
            LiveSyncEventType.RecipeUpdated,
            LiveSyncEntity.Recipes,
            id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Recipe_update_emits_recipe_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        var recipe = await SeedRecipeAsync("M9 Updated Recipe");

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{RecipesPath}/{recipe.Id}",
            """{"notes":"changed from another device"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.RecipeUpdated,
            LiveSyncEntity.Recipes,
            recipe.Id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Recipe_image_upload_emits_recipe_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var dataDir = new TempDataDir();
        using var factory = CreateFactory(Clock(), recording, dataDir.Path);
        using var client = factory.CreateClient();
        var recipe = await SeedRecipeAsync("M9 Image Upload Recipe");

        var response = await SendImageAsync(
            client,
            recipe.Id,
            Png(64, 64),
            "image/png",
            altText: null,
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.RecipeUpdated,
            LiveSyncEntity.Recipes,
            recipe.Id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Recipe_image_from_url_emits_recipe_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var dataDir = new TempDataDir();
        var fetcher = new FakeImportFetcher().OnImage(_ =>
            ImportFetchResult.Success(new Uri("https://example.com/pic.png"), 200, "image/png", Png(64, 64)));
        using var factory = CreateFactory(Clock(), recording, dataDir.Path, services =>
        {
            services.RemoveAll<IImportFetcher>();
            services.AddSingleton<IImportFetcher>(fetcher);
        });
        using var client = factory.CreateClient();
        var recipe = await SeedRecipeAsync("M9 Image Url Recipe");

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{RecipesPath}/{recipe.Id}/image/from-url",
            """{"source_url":"https://example.com/pic.png"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.RecipeUpdated,
            LiveSyncEntity.Recipes,
            recipe.Id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Recipe_image_alt_text_emits_recipe_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var dataDir = new TempDataDir();
        using var factory = CreateFactory(Clock(), recording, dataDir.Path);
        using var client = factory.CreateClient();
        var recipe = await SeedRecipeAsync("M9 Image Alt Recipe");
        (await SendImageAsync(client, recipe.Id, Png(64, 64), "image/png", altText: null, csrfToken: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Clear();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{RecipesPath}/{recipe.Id}/image",
            """{"alt_text":"a bowl of soup"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.RecipeUpdated,
            LiveSyncEntity.Recipes,
            recipe.Id.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Recipe_image_delete_emits_recipe_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var dataDir = new TempDataDir();
        using var factory = CreateFactory(Clock(), recording, dataDir.Path);
        using var client = factory.CreateClient();
        var recipe = await SeedRecipeAsync("M9 Image Delete Recipe");
        (await SendImageAsync(client, recipe.Id, Png(64, 64), "image/png", altText: null, csrfToken: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Clear();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{RecipesPath}/{recipe.Id}/image",
            json: null,
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.RecipeUpdated,
            LiveSyncEntity.Recipes,
            recipe.Id.ToString(CultureInfo.InvariantCulture));
    }

    // ---- Settings and reference data -------------------------------------

    [Fact]
    public async Task Household_settings_patch_emits_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{SettingsPath}/household",
            """{"household_name":"M9 Live Household"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.SettingsUpdated,
            LiveSyncEntity.Settings);
    }

    [Fact]
    public async Task Security_settings_patch_emits_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{SettingsPath}/security",
            """{"ip_allowlist_enabled":false}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSingleEvent(
            recording,
            LiveSyncEventType.SettingsUpdated,
            LiveSyncEntity.Settings);
    }

    [Fact]
    public async Task Auth_mode_switch_emits_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{SettingsPath}/auth-mode",
            """{"auth_mode":"multiuser","admin":{"username":"m9admin","password":"root horse battery staple"}}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var emitted = AssertSingleEvent(
            recording,
            LiveSyncEventType.SettingsUpdated,
            LiveSyncEntity.Settings);
        emitted.ActorUserId.Should().BeNull(
            "the auth-mode switch only succeeds during the single→multiuser transition, "
            + "so the caller is the single-user system actor (documented rationale in SettingsEndpoints)");
    }

    [Fact]
    public async Task Auth_mode_switch_when_already_multiuser_emits_nothing()
    {
        await SeedMultiuserAdminAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{SettingsPath}/auth-mode",
            """{"auth_mode":"multiuser","admin":{"username":"m9admin","password":"root horse battery staple"}}""",
            session.CsrfToken);

        // The route 409s before the switch transaction, so no event is emitted.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString()
            .Should().Be(ErrorCodes.AuthModeAlreadyMultiuser);
        recording.Events.Should().BeEmpty("an already-multiuser household never switches");
    }

    [Fact]
    public async Task Department_writes_emit_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            DepartmentsPath,
            """{"name":"M9 Matrix Department"}""",
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        AssertSettingsEvent(recording, LiveSyncEntity.Departments, id);
        recording.Clear();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{DepartmentsPath}/{id}",
            """{"name":"M9 Matrix Department Renamed"}""",
            csrfToken: null);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Departments, id);
        recording.Clear();

        var list = await ReadJsonAsync(await client.GetAsync($"{DepartmentsPath}?limit=100", Ct));
        var ids = list.EnumerateArray().Select(item => item.GetProperty("id").GetInt64()).ToArray();
        var reordered = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{DepartmentsPath}/order",
            JsonSerializer.Serialize(new { ids }),
            csrfToken: null);
        reordered.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Departments, id: null);
        recording.Clear();

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{DepartmentsPath}/{id}/retire",
            json: null,
            csrfToken: null);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Departments, id);
        recording.Clear();

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{DepartmentsPath}/{id}",
            json: null,
            csrfToken: null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSettingsEvent(recording, LiveSyncEntity.Departments, id);
    }

    [Fact]
    public async Task Unit_writes_emit_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            UnitsPath,
            """{"symbol":"m9x","display_name":"M9 Matrix Unit","dimension":"count"}""",
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        AssertSettingsEvent(recording, LiveSyncEntity.Units, id);
        recording.Clear();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{UnitsPath}/{id}",
            """{"display_name":"M9 Matrix Unit Renamed"}""",
            csrfToken: null);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Units, id);
        recording.Clear();

        var alias = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{UnitsPath}/{id}/aliases",
            """{"alias":"m9 alias"}""",
            csrfToken: null);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasId = (await ReadJsonAsync(alias)).GetProperty("id").GetInt64();
        AssertSettingsEvent(recording, LiveSyncEntity.Units, id);
        recording.Clear();

        var removed = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{UnitsPath}/{id}/aliases/{aliasId}",
            json: null,
            csrfToken: null);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSettingsEvent(recording, LiveSyncEntity.Units, id);
        recording.Clear();

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{UnitsPath}/{id}/retire",
            json: null,
            csrfToken: null);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Units, id);
        recording.Clear();

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{UnitsPath}/{id}",
            json: null,
            csrfToken: null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSettingsEvent(recording, LiveSyncEntity.Units, id);
    }

    [Fact]
    public async Task Ingredient_writes_emit_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"M9 Matrix Ingredient"}""",
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        AssertSettingsEvent(recording, LiveSyncEntity.Ingredients, id);
        recording.Clear();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{IngredientsPath}/{id}",
            """{"canonical_name":"M9 Matrix Ingredient Renamed"}""",
            csrfToken: null);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Ingredients, id);
        recording.Clear();

        var alias = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{IngredientsPath}/{id}/aliases",
            """{"alias":"m9 ingredient alias"}""",
            csrfToken: null);
        alias.StatusCode.Should().Be(HttpStatusCode.Created);
        var aliasId = (await ReadJsonAsync(alias)).GetProperty("id").GetInt64();
        AssertSettingsEvent(recording, LiveSyncEntity.Ingredients, id);
        recording.Clear();

        var removed = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{id}/aliases/{aliasId}",
            json: null,
            csrfToken: null);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSettingsEvent(recording, LiveSyncEntity.Ingredients, id);
        recording.Clear();

        var target = await SendJsonAsync(
            client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"M9 Matrix Ingredient Target"}""",
            csrfToken: null);
        var targetId = (await ReadJsonAsync(target)).GetProperty("id").GetInt64();
        recording.Clear();

        var merged = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{IngredientsPath}/merge",
            JsonSerializer.Serialize(new { source_id = id, target_id = targetId }),
            csrfToken: null);
        merged.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Ingredients, targetId);
        recording.Clear();

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{IngredientsPath}/{targetId}/retire",
            json: null,
            csrfToken: null);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Ingredients, targetId);
        recording.Clear();

        var disposable = await SendJsonAsync(
            client,
            HttpMethod.Post,
            IngredientsPath,
            """{"canonical_name":"M9 Matrix Ingredient Disposable"}""",
            csrfToken: null);
        var disposableId = (await ReadJsonAsync(disposable)).GetProperty("id").GetInt64();
        recording.Clear();

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{IngredientsPath}/{disposableId}",
            json: null,
            csrfToken: null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSettingsEvent(recording, LiveSyncEntity.Ingredients, disposableId);
    }

    [Fact]
    public async Task Protein_writes_emit_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ProteinsPath,
            """{"name":"M9 Matrix Protein"}""",
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        AssertSettingsEvent(recording, LiveSyncEntity.Proteins, id);
        recording.Clear();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{ProteinsPath}/{id}",
            """{"name":"M9 Matrix Protein Renamed"}""",
            csrfToken: null);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Proteins, id);
        recording.Clear();

        var retired = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{ProteinsPath}/{id}/retire",
            json: null,
            csrfToken: null);
        retired.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Proteins, id);
        recording.Clear();

        var disposable = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ProteinsPath,
            """{"name":"M9 Matrix Protein Disposable"}""",
            csrfToken: null);
        var disposableId = (await ReadJsonAsync(disposable)).GetProperty("id").GetInt64();
        recording.Clear();

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{ProteinsPath}/{disposableId}",
            json: null,
            csrfToken: null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSettingsEvent(recording, LiveSyncEntity.Proteins, disposableId);
    }

    [Fact]
    public async Task Tag_writes_emit_settings_updated()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var created = await SendJsonAsync(
            client,
            HttpMethod.Post,
            TagsPath,
            """{"name":"M9 Matrix Tag"}""",
            csrfToken: null);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = (await ReadJsonAsync(created)).GetProperty("id").GetInt64();
        AssertSettingsEvent(recording, LiveSyncEntity.Tags, id);
        recording.Clear();

        var updated = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{TagsPath}/{id}",
            """{"name":"M9 Matrix Tag Renamed"}""",
            csrfToken: null);
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertSettingsEvent(recording, LiveSyncEntity.Tags, id);
        recording.Clear();

        var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{TagsPath}/{id}",
            json: null,
            csrfToken: null);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        AssertSettingsEvent(recording, LiveSyncEntity.Tags, id);
    }

    // ---- Negative cases --------------------------------------------------

    [Fact]
    public async Task Validation_error_write_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"recipe"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        recording.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Not_found_write_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{GroceriesPath}/manual/999999",
            """{"name":"M9 Ghost"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        recording.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Rolled_back_conflict_write_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var first = await SendJsonAsync(
            client,
            HttpMethod.Post,
            RecipesPath,
            """{"name":"M9 Duplicate Recipe"}""",
            csrfToken: null);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        recording.Clear();

        var second = await SendJsonAsync(
            client,
            HttpMethod.Post,
            RecipesPath,
            """{"name":"M9 Duplicate Recipe"}""",
            csrfToken: null);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        recording.Events.Should().BeEmpty("a rolled-back conflict emits nothing");
    }

    [Fact]
    public async Task Rolled_back_batch_apply_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();
        await SeedRecipeAsync("M9 Batch Rollback One");
        await SeedRecipeAsync("M9 Batch Rollback Two");

        var preview = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            """{"start_date":"2026-11-15","mode":"seven_days"}""",
            csrfToken: null);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await ReadJsonAsync(preview)).GetProperty("draft").GetProperty("token").GetString()!;
        recording.Events.Should().BeEmpty("a preview persists only a draft");

        // Change one of the previewed target dates so the apply's captured
        // before-state no longer matches: the whole batch aborts and rolls back
        // with BATCH_CONFLICT and emits nothing.
        var mutate = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"eat_out"}""",
            csrfToken: null);
        mutate.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Clear();

        var apply = await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/apply",
            JsonSerializer.Serialize(new { token }),
            csrfToken: null);

        apply.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadJsonAsync(apply)).GetProperty("error").GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchConflict);
        recording.Events.Should().BeEmpty("a rolled-back batch apply emits nothing");
    }

    [Fact]
    public async Task Single_user_write_emits_the_event_with_a_null_system_actor()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"eat_out"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entryId = (await ReadJsonAsync(response)).GetProperty("entry").GetProperty("id").GetInt64();
        var emitted = AssertSingleEvent(
            recording,
            LiveSyncEventType.CalendarUpdated,
            LiveSyncEntity.Calendar,
            entryId.ToString(CultureInfo.InvariantCulture));
        emitted.ActorUserId.Should().BeNull(
            "single-user mode has no user row and acts as the system actor (docs/M9_PLAN.md §1.3)");
    }

    [Fact]
    public async Task Read_request_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"{CalendarPath}?start={Format(Today)}&end={Format(Today.AddDays(6))}",
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_draft_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        var fetcher = new FakeImportFetcher().OnPageHtml(
            """
            <html><head>
            <script type="application/ld+json">
            {"@context":"https://schema.org","@type":"Recipe","name":"M9 Import","recipeIngredient":["1 cup flour"],"recipeInstructions":[{"@type":"HowToStep","text":"Mix."}]}
            </script></head><body></body></html>
            """,
            new Uri("https://example.com/recipes/m9"));
        using var factory = CreateFactory(Clock(), recording, dataDir: null, services =>
        {
            services.RemoveAll<IImportFetcher>();
            services.AddSingleton<IImportFetcher>(fetcher);
        });
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ImportPath,
            """{"url":"https://example.com/recipes/m9"}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Events.Should().BeEmpty("the import route persists no recipe");
    }

    [Fact]
    public async Task Import_check_read_emits_nothing()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ImportCheckPath,
            """{"source_url":null,"name":null}""",
            csrfToken: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        recording.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Household_writes_never_emit_session_revoked()
    {
        await SeedSingleUserHouseholdAsync();
        var recording = new RecordingLiveSyncPublisher();
        using var factory = CreateFactory(Clock(), recording);
        using var client = factory.CreateClient();

        await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{CalendarPath}/{Format(Today)}",
            """{"entry_type":"eat_out"}""",
            csrfToken: null);
        await SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{GroceriesPath}/manual",
            ManualJson("M9 No Revoke"),
            csrfToken: null);
        await SendJsonAsync(
            client,
            HttpMethod.Patch,
            $"{SettingsPath}/security",
            """{"ip_allowlist_enabled":false}""",
            csrfToken: null);

        recording.Events.Should().NotBeEmpty();
        recording.Events.Should().OnlyContain(evt => evt.Type != LiveSyncEventType.SessionRevoked);
        recording.RevokedSessionIds.Should().BeEmpty(
            "household writes never revoke a session; M9-06 only wires logout/user-admin flows");
        recording.RevokedUserSessions.Should().BeEmpty(
            "household writes never revoke a user's sessions");
    }

    // ---- Completeness guard ----------------------------------------------

    /// <summary>
    /// Every mutating route registered by the app must be explicitly covered or
    /// excluded here; adding a write route without deciding its emission fails
    /// this test. Route patterns are compared canonically: inline constraints
    /// are stripped (<c>{id:long}</c> == <c>{id}</c>) and the trailing slash an
    /// empty route suffix produces is trimmed (<c>/api/v1/users/</c> ==
    /// <c>/api/v1/users</c>).
    /// </summary>
    [Fact]
    public void Event_matrix_covers_every_mutating_route()
    {
        using var factory = fixture.CreateFactory(liveSyncPublisher: new RecordingLiveSyncPublisher());
        var covered = new HashSet<string>(
            CoveredMutatingRoutes.Select(CanonicalRoute),
            StringComparer.Ordinal);
        var excluded = new HashSet<string>(
            ExcludedMutatingRoutes.Select(CanonicalRoute),
            StringComparer.Ordinal);

        covered.Should().NotIntersectWith(excluded);

        var discovered = DiscoverMutatingRoutes(factory);
        discovered.Should().NotBeEmpty();
        discovered.Should().BeEquivalentTo(
            covered.Concat(excluded),
            because: "every mutating route must be covered by an emission or explicitly excluded");
    }

    /// <summary>
    /// Routes that emit an event. Each entry is <c>METHOD /path</c>.
    /// </summary>
    private static readonly string[] CoveredMutatingRoutes =
    [
        // calendar.updated
        "PUT /api/v1/calendar/{date}",
        "DELETE /api/v1/calendar/{date}",
        "POST /api/v1/calendar/{date}/reroll",
        "POST /api/v1/calendar/reroll/apply",
        "POST /api/v1/calendar/{date}/correction",
        "POST /api/v1/operations/{operation_id}/undo",
        "POST /api/v1/batch-operations/{batch_operation_id}/undo",
        // calendar.lock_changed (and only that)
        "POST /api/v1/calendar/{date}/lock",
        "POST /api/v1/calendar/{date}/unlock",
        // groceries.updated
        "POST /api/v1/groceries/manual",
        "PATCH /api/v1/groceries/manual/{id}",
        "DELETE /api/v1/groceries/manual/{id}",
        "POST /api/v1/groceries/staples",
        "PATCH /api/v1/groceries/staples/{id}",
        "POST /api/v1/groceries/staples/{id}/add-to-list",
        "POST /api/v1/groceries/check",
        "POST /api/v1/groceries/uncheck",
        // recipe.updated
        "POST /api/v1/recipes",
        "PATCH /api/v1/recipes/{id}",
        "PUT /api/v1/recipes/{id}/image",
        "POST /api/v1/recipes/{id}/image/from-url",
        "PATCH /api/v1/recipes/{id}/image",
        "DELETE /api/v1/recipes/{id}/image",
        "DELETE /api/v1/recipes/{id}",
        // settings.updated
        "PATCH /api/v1/settings/household",
        "POST /api/v1/settings/auth-mode",
        "PATCH /api/v1/settings/security",
        "POST /api/v1/departments",
        "PATCH /api/v1/departments/{id}",
        "POST /api/v1/departments/{id}/retire",
        "DELETE /api/v1/departments/{id}",
        "PUT /api/v1/departments/order",
        "POST /api/v1/units",
        "PATCH /api/v1/units/{id}",
        "POST /api/v1/units/{id}/aliases",
        "DELETE /api/v1/units/{id}/aliases/{aliasId}",
        "POST /api/v1/units/{id}/retire",
        "DELETE /api/v1/units/{id}",
        "POST /api/v1/ingredients",
        "POST /api/v1/ingredients/merge",
        "PATCH /api/v1/ingredients/{id}",
        "POST /api/v1/ingredients/{id}/aliases",
        "DELETE /api/v1/ingredients/{id}/aliases/{aliasId}",
        "POST /api/v1/ingredients/{id}/retire",
        "DELETE /api/v1/ingredients/{id}",
        "POST /api/v1/proteins",
        "PATCH /api/v1/proteins/{id}",
        "POST /api/v1/proteins/{id}/retire",
        "DELETE /api/v1/proteins/{id}",
        "POST /api/v1/tags",
        "PATCH /api/v1/tags/{id}",
        "DELETE /api/v1/tags/{id}",
    ];

    /// <summary>
    /// Mutating routes that intentionally emit nothing, with the reason.
    /// </summary>
    private static readonly string[] ExcludedMutatingRoutes =
    [
        // Persists a draft only; changes no committed calendar data.
        "POST /api/v1/calendar/reroll/preview",
        // Per-user private preferences, never broadcast.
        "PATCH /api/v1/groceries/preferences",
        // Import draft / read; no recipe is persisted.
        "POST /api/v1/import/recipe-url",
        "POST /api/v1/import/check",
        // Authentication flows; session.revoked is M9-06's job.
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/change-password",
        // Setup is not a household settings write.
        "POST /api/v1/setup/complete",
        // User/permission management is out of M9's settings scope; user
        // disable also owns session.revoked in M9-06.
        "POST /api/v1/users",
        "PATCH /api/v1/users/{id}",
        "PATCH /api/v1/users/{id}/permissions",
        "POST /api/v1/users/{id}/disable",
        "POST /api/v1/users/{id}/enable",
        "POST /api/v1/users/{id}/reset-password",
        // Audit-log maintenance: deletes old audit rows and records its own
        // audit.pruned row. No audit live-sync entity exists, so the admin
        // audit viewer simply refetches.
        "POST /api/v1/admin/audit/prune",
        // Backup maintenance (M13): archives are admin-local files with no
        // live-sync entity; the backups list refetches after a create/delete.
        "POST /api/v1/admin/backups",
        "DELETE /api/v1/admin/backups/{id}",
        // Restore replaces the database and then restarts the process, so every
        // socket is torn down; there is nothing to broadcast to live clients.
        "POST /api/v1/admin/backups/restore",
        "POST /api/v1/setup/restore",
        // Recovery-key export is a guarded download that mutates nothing.
        "POST /api/v1/admin/recovery-key/export",
        // Update-check state is admin-local application settings; the About
        // section refetches, and no entity clients cache is affected.
        "POST /api/v1/admin/updates/check",
        "PATCH /api/v1/admin/updates",
    ];

    private static HashSet<string> DiscoverMutatingRoutes(ApiFactory factory)
    {
        var sources = factory.Services.GetServices<EndpointDataSource>().ToList();
        if (sources.Count == 0 && factory.Services.GetService<EndpointDataSource>() is { } single)
        {
            sources.Add(single);
        }

        var routes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in sources.SelectMany(source => source.Endpoints))
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            var pattern = route.RoutePattern.RawText;
            if (string.IsNullOrEmpty(pattern) || pattern.StartsWith("/hub/", StringComparison.Ordinal))
            {
                continue;
            }

            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null)
            {
                continue;
            }

            foreach (var method in methods)
            {
                if (method is "GET" or "HEAD" or "OPTIONS" or "TRACE")
                {
                    continue;
                }

                routes.Add($"{method} {CanonicalRoute(pattern)}");
            }
        }

        return routes;
    }

    private static string CanonicalRoute(string pattern) =>
        StripConstraints(pattern).TrimEnd('/');

    private static string StripConstraints(string pattern) =>
        Regex.Replace(pattern, @"\{([^}:]+):[^}]+\}", "{$1}");

    // ---- Two-client broadcast --------------------------------------------

    [Fact]
    public async Task Household_group_receives_every_event_type_over_signalr()
    {
        var admin = await SeedMultiuserAdminAsync();
        using var factory = fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock());
        });

        var recipe = await SeedRecipeAsync("M9 Broadcast Recipe");

        using var sessionA = await SignInAsync(factory, "root", AdminPassword);
        using var sessionB = await SignInAsync(factory, "root", AdminPassword);

        await using var connectionA = CreateHubConnection(
            factory,
            sessionA.Cookies.GetCookieHeader(factory.Server.BaseAddress));
        await using var connectionB = CreateHubConnection(
            factory,
            sessionB.Cookies.GetCookieHeader(factory.Server.BaseAddress));

        var clientA = TrackEvents(connectionA);
        var clientB = TrackEvents(connectionB);

        await connectionA.StartAsync(Ct);
        await connectionB.StartAsync(Ct);
        await WaitForConnectionsAsync(factory, 2);

        var writes = new (string Type, Func<Task<HttpResponseMessage>> Send)[]
        {
            (LiveSyncEventType.CalendarUpdated, () => SendJsonAsync(
                sessionA.Client,
                HttpMethod.Put,
                $"{CalendarPath}/{Format(Today)}",
                JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipe.Id }),
                sessionA.CsrfToken)),
            (LiveSyncEventType.CalendarLockChanged, () => SendJsonAsync(
                sessionA.Client,
                HttpMethod.Post,
                $"{CalendarPath}/{Format(Today)}/lock",
                json: null,
                sessionA.CsrfToken)),
            (LiveSyncEventType.GroceriesUpdated, () => SendJsonAsync(
                sessionA.Client,
                HttpMethod.Post,
                $"{GroceriesPath}/manual",
                ManualJson("M9 Broadcast Grocery"),
                sessionA.CsrfToken)),
            (LiveSyncEventType.RecipeUpdated, () => SendJsonAsync(
                sessionA.Client,
                HttpMethod.Patch,
                $"{RecipesPath}/{recipe.Id}",
                """{"notes":"broadcast"}""",
                sessionA.CsrfToken)),
            (LiveSyncEventType.SettingsUpdated, () => SendJsonAsync(
                sessionA.Client,
                HttpMethod.Patch,
                $"{SettingsPath}/security",
                """{"ip_allowlist_enabled":false}""",
                sessionA.CsrfToken)),
        };

        foreach (var (type, send) in writes)
        {
            var response = await send();
            response.IsSuccessStatusCode.Should().BeTrue();

            var eventA = await clientA.NextAsync(Ct);
            var eventB = await clientB.NextAsync(Ct);
            eventA.Type.Should().Be(type);
            eventB.Type.Should().Be(type);
            eventA.ActorUserId.Should().Be(admin.Id);
        }

        // The envelope is snake_case on the wire.
        clientA.Raw.Should().NotBeEmpty();
        var raw = clientA.Raw.First();
        raw.TryGetProperty("actor_user_id", out _).Should().BeTrue();
        raw.TryGetProperty("timestamp", out _).Should().BeTrue();
        raw.TryGetProperty("type", out _).Should().BeTrue();
        raw.TryGetProperty("operation_id", out _).Should().BeTrue();
    }

    // ---- Helpers ---------------------------------------------------------

    private static TestTimeProvider Clock() => new(Now);

    private ApiFactory CreateFactory(
        TestTimeProvider clock,
        RecordingLiveSyncPublisher recording,
        string? dataDir = null,
        Action<IServiceCollection>? configure = null) =>
        fixture.CreateFactory(
            dataDir,
            services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                configure?.Invoke(services);
            },
            recording);

    private static LiveSyncEvent AssertSingleEvent(
        RecordingLiveSyncPublisher recording,
        string type,
        string entity,
        string? id = null,
        string? operationId = null,
        long? actorUserId = null)
    {
        var events = recording.Events.ToList();
        events.Should().ContainSingle();
        var liveSyncEvent = events[0];
        liveSyncEvent.Type.Should().Be(type);
        liveSyncEvent.Entity.Should().Be(entity);
        liveSyncEvent.Id.Should().Be(id);
        liveSyncEvent.OperationId.Should().Be(operationId);
        liveSyncEvent.ActorUserId.Should().Be(actorUserId);
        liveSyncEvent.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        liveSyncEvent.Timestamp.Offset.Should().Be(TimeSpan.Zero);
        return liveSyncEvent;
    }

    private static void AssertSettingsEvent(
        RecordingLiveSyncPublisher recording,
        string entity,
        long? id) =>
        AssertSingleEvent(
            recording,
            LiveSyncEventType.SettingsUpdated,
            entity,
            id?.ToString(CultureInfo.InvariantCulture));

    private static string ManualJson(string name) =>
        JsonSerializer.Serialize(new
        {
            name,
            range_start = Format(Today),
            range_days = 7,
        });

    private static string Format(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private static async Task<HttpResponseMessage> SendImageAsync(
        HttpClient client,
        long recipeId,
        byte[] bytes,
        string contentType,
        string? altText,
        string? csrfToken)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(file, "file", "image.png");
        if (altText is not null)
        {
            content.Add(new StringContent(altText), "alt_text");
        }

        using var request = new HttpRequestMessage(HttpMethod.Put, $"{RecipesPath}/{recipeId}/image")
        {
            Content = content,
        };
        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private async Task SeedSingleUserHouseholdAsync()
    {
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
    }

    private async Task<User> SeedMultiuserAdminAsync()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        var user = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
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

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M9 Live Household",
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

    private static async Task WaitForConnectionsAsync(ApiFactory factory, int expected)
    {
        var registry = factory.Services.GetRequiredService<LiveSyncConnectionRegistry>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (registry.Count < expected)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expected} live-sync connection(s).");
            }

            await Task.Delay(20, Ct);
        }
    }

    private static HubConnection CreateHubConnection(
        WebApplicationFactory<Program> factory,
        string cookieHeader) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, LiveSyncPaths.HubPath), options =>
            {
                // Long polling keeps the connection on the TestServer's in-process
                // handler; the SPA uses skipNegotiation WebSockets in production.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers["Cookie"] = cookieHeader;
            })
            .Build();

    private static TrackedClient TrackEvents(HubConnection connection)
    {
        var tracked = new TrackedClient();
        // SignalR invokes only the first typed handler registered for a method
        // name, so capture the raw JSON envelope and derive the typed DTO from
        // it instead of registering two handlers on "event" (the second would
        // never run).
        connection.On<JsonElement>(SignalRLiveSyncPublisher.ClientMethod, raw =>
        {
            var payload = raw.Clone();
            tracked.Raw.Enqueue(payload);
            var evt = JsonSerializer.Deserialize<LiveSyncEventDto>(payload);
            if (evt is not null)
            {
                tracked.Events.Enqueue(evt);
            }
        });
        return tracked;
    }

    private static byte[] Png(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(stream, "IHDR"u8, PngHeader(width, height));
        WritePngChunk(stream, "IDAT"u8, [0x78, 0x9C]);
        WritePngChunk(stream, "IEND"u8, []);
        return stream.ToArray();
    }

    private static byte[] PngHeader(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        return header;
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(type);
        stream.Write(data);
        stream.Write(new byte[4]);
    }

    private sealed class LiveSyncEntity
    {
        public const string Calendar = "calendar";
        public const string Groceries = "groceries";
        public const string Recipes = "recipes";
        public const string Settings = "settings";
        public const string Departments = "departments";
        public const string Units = "units";
        public const string Ingredients = "ingredients";
        public const string Proteins = "proteins";
        public const string Tags = "tags";
    }

    private sealed class LiveSyncEventDto
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("entity")]
        public string? Entity { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("operation_id")]
        public string? OperationId { get; set; }

        [JsonPropertyName("actor_user_id")]
        public long? ActorUserId { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }
    }

    private sealed class TrackedClient
    {
        public ConcurrentQueue<LiveSyncEventDto> Events { get; } = new();

        public ConcurrentQueue<JsonElement> Raw { get; } = new();

        public async Task<LiveSyncEventDto> NextAsync(CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (true)
            {
                if (Events.TryDequeue(out var evt))
                {
                    return evt;
                }

                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException("No live-sync event was received.");
                }

                await Task.Delay(20, cancellationToken);
            }
        }
    }

    private sealed class SignedInClient(HttpClient client, CookieContainer cookies, string csrfToken)
        : IDisposable
    {
        public HttpClient Client { get; } = client;

        public CookieContainer Cookies { get; } = cookies;

        public string CsrfToken { get; } = csrfToken;

        public void Dispose() => Client.Dispose();
    }

    private sealed class TempDataDir : IDisposable
    {
        public TempDataDir()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "faf-m9-" + Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
