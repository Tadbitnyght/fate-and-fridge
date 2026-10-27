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
/// HTTP-level verification of the M6 undo route (docs/M6_PLAN.md §1.13):
/// exact restore of the pre-reroll entry or blank date, the later-change
/// conflict, expiry/idempotency, malformed ids, the permission matrix, and that
/// undo writes exactly one history row and no audit rows. Helpers mirror the
/// reroll endpoint tests; the clock is injected so "today" is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class UndoEndpointsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string OperationsPath = "/api/v1/operations";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "undo-endpoints-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM calendar_entry_history; DELETE FROM calendar_entries;",
                Ct);
        }

        await fixture.ResetAsync(Ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Undo_restores_the_exact_prior_entry()
    {
        await SeedAdminHouseholdAsync();
        var chosen = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(
            Today,
            CalendarEntryType.Misc,
            recipeId: null,
            restaurantName: "Cafe",
            locationText: "Downtown",
            customLabel: "Leftovers",
            iconKey: "star",
            dinerCount: 3,
            notes: "keep me",
            includeGroceries: false);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = await RerollAndCaptureAsync(session, Today);

        // The reroll replaced the Misc entry with the only Normal recipe.
        var rerolled = await ReadDayAsync(session, Today);
        rerolled.GetProperty("entry").GetProperty("entry_type").GetString().Should().Be("recipe");
        rerolled.GetProperty("entry").GetProperty("recipe").GetProperty("id").GetInt64()
            .Should().Be(chosen.Id);

        var response = await UndoAsync(session, operationId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var day = await ReadJsonAsync(response);
        day.GetProperty("date").GetString().Should().Be(Today.ToString("yyyy-MM-dd"));

        var entry = day.GetProperty("entry");
        entry.GetProperty("entry_type").GetString().Should().Be("misc");
        entry.GetProperty("recipe").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("restaurant_name").GetString().Should().Be("Cafe");
        entry.GetProperty("location_text").GetString().Should().Be("Downtown");
        entry.GetProperty("custom_label").GetString().Should().Be("Leftovers");
        entry.GetProperty("icon_key").GetString().Should().Be("star");
        entry.GetProperty("diner_count").GetInt32().Should().Be(3);
        entry.GetProperty("notes").GetString().Should().Be("keep me");
        entry.GetProperty("include_groceries").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Undo_of_a_reroll_over_a_blank_date_leaves_the_date_blank()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = await RerollAndCaptureAsync(session, Today);

        var response = await UndoAsync(session, operationId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var day = await ReadJsonAsync(response);
        day.GetProperty("entry").ValueKind.Should().Be(JsonValueKind.Null);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.AnyAsync(candidate => candidate.MealDate == Today, Ct))
            .Should().BeFalse();
        var history = await context.CalendarEntryHistory.OrderBy(row => row.Id).ToListAsync(Ct);
        history.Should().HaveCount(2);
        var undoRow = history[1];
        undoRow.ChangeReason.Should().Be($"undo:{operationId:D}");
        undoRow.AfterJson.Should().BeNull();
        undoRow.CalendarEntryId.Should().BeNull();
        undoRow.BeforeJson.Should().NotBeNull();
    }

    [Fact]
    public async Task Undo_after_a_later_lock_conflicts()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = await RerollAndCaptureAsync(session, Today);
        var lockResponse = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{Today:yyyy-MM-dd}/lock",
            json: null,
            session.CsrfToken);
        lockResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await UndoAsync(session, operationId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UndoConflict);
    }

    [Fact]
    public async Task Undo_after_a_later_upsert_conflicts()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = await RerollAndCaptureAsync(session, Today);
        var put = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{Today:yyyy-MM-dd}",
            JsonSerializer.Serialize(new { entry_type = "fend_for_yourself" }),
            session.CsrfToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await UndoAsync(session, operationId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UndoConflict);
    }

    [Fact]
    public async Task Undo_twice_returns_expired()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = await RerollAndCaptureAsync(session, Today);
        (await UndoAsync(session, operationId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await UndoAsync(session, operationId);

        second.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadErrorAsync(second)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UndoExpired);
    }

    [Fact]
    public async Task Undo_of_an_unknown_operation_returns_expired()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await UndoAsync(session, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UndoExpired);
    }

    [Fact]
    public async Task Undo_with_a_malformed_id_returns_validation_failed()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{OperationsPath}/not-a-guid/undo",
            json: null,
            session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").GetProperty("operation_id")
            .GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Undo_permission_matrix()
    {
        await SeedHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var editor = await SeedUserAsync("editor", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(editor.Id, PermissionKeys.CalendarEdit);
        await SeedUserAsync("nobody", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var editorSession = await SignInAsync(factory, "editor", MemberPassword))
        {
            var operationId = await RerollAndCaptureAsync(editorSession, Today);
            var allowed = await UndoAsync(editorSession, operationId);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            var denied = await UndoAsync(deniedSession, Guid.NewGuid());
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorAsync(denied)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        using var anonymous = factory.CreateClient();
        (await UndoAsync(anonymous, Guid.NewGuid(), csrfToken: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Undo_writes_one_history_row_and_no_audit_rows()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(
            Today,
            CalendarEntryType.EatOut,
            recipeId: null,
            restaurantName: "Cafe",
            dinerCount: 2);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = await RerollAndCaptureAsync(session, Today);
        (await UndoAsync(session, operationId)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.OrderBy(row => row.Id).ToListAsync(Ct);
        history.Should().HaveCount(2);
        history.Single(row => row.OperationId == operationId);

        var undoRow = history.Single(row => row.ChangeReason == $"undo:{operationId:D}");
        undoRow.OperationId.Should().NotBe(operationId);
        undoRow.BeforeJson.Should().NotBeNull();
        undoRow.AfterJson.Should().NotBeNull();
        undoRow.IsHistoricalCorrection.Should().BeFalse();
        undoRow.MetadataJson.Should().NotBeNull();

        using var document = JsonDocument.Parse(undoRow.MetadataJson!);
        document.RootElement.GetProperty("kind").GetString().Should().Be("undo");
        document.RootElement.GetProperty("undone_operation_id").GetString()
            .Should().Be(operationId.ToString());

        (await context.AuditEvents.CountAsync(row => !row.EventType.StartsWith("auth."), Ct)).Should().Be(
            0,
            "undo stages no audit row; M12-01 only mirrors auth events");
    }

    [Fact]
    public async Task Undo_of_a_non_reroll_operation_returns_expired()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = Guid.NewGuid();
        await SeedHistoryAsync(Today, operationId, """{"kind":"upsert"}""");

        var response = await UndoAsync(session, operationId);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UndoExpired);
    }

    [Fact]
    public async Task Undo_of_a_reroll_that_is_now_historical_returns_date_in_past()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        var clock = new TestTimeProvider(Now);
        using var factory = CreateFactory(clock);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var future = Today.AddDays(3);
        var operationId = await RerollAndCaptureAsync(session, future);

        // Move the household clock past the rerolled date so it is now historical.
        clock.UtcNow = Now.AddDays(5);

        var response = await UndoAsync(session, operationId);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);
    }

    [Fact]
    public async Task Concurrent_undoes_of_the_same_operation_apply_once()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var operationId = await RerollAndCaptureAsync(session, Today);

        var responses = await Task.WhenAll(
            UndoAsync(session, operationId),
            UndoAsync(session, operationId));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Should().OnlyContain(response =>
            response.StatusCode == HttpStatusCode.OK
                || response.StatusCode == HttpStatusCode.Gone
                || response.StatusCode == HttpStatusCode.Conflict);
        responses.Should().NotContain(response =>
            response.StatusCode == HttpStatusCode.InternalServerError);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntryHistory.CountAsync(
            row => row.ChangeReason == $"undo:{operationId:D}", Ct)).Should().Be(1);
    }

    private async Task SeedHistoryAsync(DateOnly date, Guid operationId, string metadataJson)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntryHistory.Add(new CalendarEntryHistory
        {
            MealDate = date,
            OperationId = operationId,
            MetadataJson = metadataJson,
            ChangedAt = SeedTimestamp,
            IsHistoricalCorrection = false,
        });
        await context.SaveChangesAsync(Ct);
    }

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private static Task<HttpResponseMessage> RerollAsync(SignedInClient session, DateOnly date) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date:yyyy-MM-dd}/reroll",
            json: null,
            session.CsrfToken);

    private async Task<Guid> RerollAndCaptureAsync(SignedInClient session, DateOnly date)
    {
        var response = await RerollAsync(session, date);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        return Guid.Parse(json.GetProperty("operation").GetProperty("id").GetString()!);
    }

    private async Task<JsonElement> ReadDayAsync(SignedInClient session, DateOnly date)
    {
        var response = await session.Client.GetAsync($"{CalendarPath}/{date:yyyy-MM-dd}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response);
    }

    private static Task<HttpResponseMessage> UndoAsync(SignedInClient session, Guid operationId) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{OperationsPath}/{operationId:D}/undo",
            json: null,
            session.CsrfToken);

    private static Task<HttpResponseMessage> UndoAsync(
        HttpClient client,
        Guid operationId,
        string? csrfToken) =>
        SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{OperationsPath}/{operationId:D}/undo",
            json: null,
            csrfToken);

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
            HouseholdName = "Undo Household",
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
        string? notes = null,
        bool includeGroceries = false,
        string? restaurantName = null,
        string? locationText = null,
        string? customLabel = null,
        string? iconKey = null)
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
            IncludeGroceries = includeGroceries,
            RestaurantName = restaurantName,
            LocationText = locationText,
            CustomLabel = customLabel,
            IconKey = iconKey,
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
