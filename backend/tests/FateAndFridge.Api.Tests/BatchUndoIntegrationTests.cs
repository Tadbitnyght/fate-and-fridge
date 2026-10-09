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
/// HTTP-level verification of the M7 batch undo (docs/M7_PLAN.md §1.10, §2.2):
/// exact multi-date restore (including a replaced recipe's prior fields and a
/// blank date returning to blank), one undo history row per restored date sharing
/// one fresh batch operation id, the later-change conflict, expiry/idempotency,
/// now-historical dates, malformed ids, the permission matrix, and that undo
/// writes no audit rows. The clock is injected so "today" is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class BatchUndoIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string PreviewPath = "/api/v1/calendar/reroll/preview";
    private const string ApplyPath = "/api/v1/calendar/reroll/apply";
    private const string BatchOperationsPath = "/api/v1/batch-operations";
    private const string CalendarPath = "/api/v1/calendar";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "batch-undo-test";

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

    [Fact]
    public async Task Undo_restores_every_changed_date_exactly()
    {
        await SeedAdminHouseholdAsync();
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(
            Today.AddDays(1),
            CalendarEntryType.Recipe,
            lasagna.Id,
            dinerCount: 2,
            notes: "old note",
            restaurantName: "Cafe",
            locationText: "Downtown",
            includeGroceries: true);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var changedDates = ProposedByDate(preview).Keys.OrderBy(date => date).ToArray();
        var apply = await ReadJsonAsync(await ApplyAsync(session, TokenOf(preview)));

        var response = await UndoAsync(session, BatchIdOf(apply));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("reverted_count").GetInt32().Should().Be(changedDates.Length);

        var days = root.GetProperty("days").EnumerateArray().ToArray();
        days.Should().HaveCount(changedDates.Length);
        days.Select(ParseDate).Should().BeInAscendingOrder();

        // The replaced date is restored field-for-field; the replaced recipe is
        // the one that was there before the apply, not the applied recipe.
        var replaced = days.Single(day => ParseDate(day) == Today.AddDays(1)).GetProperty("entry");
        replaced.GetProperty("entry_type").GetString().Should().Be("recipe");
        replaced.GetProperty("recipe").GetProperty("id").GetInt64().Should().Be(lasagna.Id);
        replaced.GetProperty("diner_count").GetInt32().Should().Be(2);
        replaced.GetProperty("notes").GetString().Should().Be("old note");
        replaced.GetProperty("restaurant_name").GetString().Should().Be("Cafe");
        replaced.GetProperty("location_text").GetString().Should().Be("Downtown");
        replaced.GetProperty("include_groceries").GetBoolean().Should().BeTrue();

        // Every other changed date was blank before the apply and is blank again.
        foreach (var day in days)
        {
            if (ParseDate(day) == Today.AddDays(1))
            {
                continue;
            }

            day.GetProperty("entry").ValueKind.Should().Be(JsonValueKind.Null);
        }

        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Should().ContainSingle();
        stored[0].MealDate.Should().Be(Today.AddDays(1));
        stored[0].RecipeId.Should().Be(lasagna.Id);
    }

    [Fact]
    public async Task Undo_writes_one_history_row_per_date_sharing_one_new_batch_operation_id()
    {
        await SeedAdminHouseholdAsync();
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.Recipe, lasagna.Id, includeGroceries: true);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var changedDates = ProposedByDate(preview).Keys.OrderBy(date => date).ToArray();
        var apply = await ReadJsonAsync(await ApplyAsync(session, TokenOf(preview)));
        var batchOperationId = BatchIdOf(apply);

        (await UndoAsync(session, batchOperationId)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.AsNoTracking().ToListAsync(Ct);
        history.Should().HaveCount(changedDates.Length * 2, "one apply row and one undo row per changed date");

        var undoRows = history.Where(row => row.ChangeReason == $"undo:{batchOperationId:D}").ToList();
        undoRows.Should().HaveCount(changedDates.Length);
        undoRows.Select(row => row.BatchOperationId).Distinct()
            .Should().ContainSingle("every undo row shares one fresh batch operation id")
            .Which.Should().NotBe(batchOperationId);
        undoRows.Select(row => row.OperationId).Distinct()
            .Should().HaveCount(changedDates.Length, "each undo row keeps its own operation id");

        foreach (var row in undoRows)
        {
            row.IsHistoricalCorrection.Should().BeFalse();
            row.MetadataJson.Should().NotBeNull();
            using var document = JsonDocument.Parse(row.MetadataJson!);
            document.RootElement.GetProperty("kind").GetString().Should().Be("undo");
            document.RootElement.GetProperty("undone_batch_operation_id").GetString()
                .Should().Be(batchOperationId.ToString());
        }

        var blankDate = changedDates.First(date => date != Today.AddDays(2));
        var blankUndo = undoRows.Single(row => row.MealDate == blankDate);
        blankUndo.BeforeJson.Should().NotBeNull("the applied entry is snapshotted");
        blankUndo.AfterJson.Should().BeNull("the date is restored to blank");
        blankUndo.CalendarEntryId.Should().BeNull();

        var replacedUndo = undoRows.Single(row => row.MealDate == Today.AddDays(2));
        replacedUndo.BeforeJson.Should().NotBeNull();
        replacedUndo.AfterJson.Should().NotBeNull();
        replacedUndo.CalendarEntryId.Should().NotBeNull();

        (await context.AuditEvents.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Undo_after_a_later_change_to_one_date_conflicts_and_reverts_nothing()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var changedDates = ProposedByDate(preview).Keys.OrderBy(date => date).ToArray();
        var apply = await ReadJsonAsync(await ApplyAsync(session, TokenOf(preview)));
        var batchOperationId = BatchIdOf(apply);
        var appliedByDate = AppliedByDate(apply);

        var changedDate = changedDates[2];
        var put = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{changedDate:yyyy-MM-dd}",
            JsonSerializer.Serialize(new { entry_type = "fend_for_yourself" }),
            session.CsrfToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await UndoAsync(session, batchOperationId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UndoConflict);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntryHistory.CountAsync(
            row => row.ChangeReason == $"undo:{batchOperationId:D}", Ct))
            .Should().Be(0, "a conflict reverts nothing");

        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        foreach (var (date, recipeId) in appliedByDate)
        {
            if (date == changedDate)
            {
                continue;
            }

            stored.Single(entry => entry.MealDate == date).RecipeId.Should().Be(
                recipeId,
                "dates other than the conflicting one are untouched");
        }

        stored.Single(entry => entry.MealDate == changedDate).EntryType
            .Should().Be(CalendarEntryType.FendForYourself);
    }

    [Fact]
    public async Task Undo_twice_returns_expired()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var batchOperationId = BatchIdOf(await ReadJsonAsync(await ApplyAsync(session, TokenOf(preview))));

        (await UndoAsync(session, batchOperationId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await UndoAsync(session, batchOperationId);

        second.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadErrorAsync(second)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.UndoExpired);
    }

    [Fact]
    public async Task Concurrent_undoes_of_the_same_batch_apply_once_and_the_loser_is_expired()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var changedDates = ProposedByDate(preview).Keys.ToArray();
        var batchOperationId = BatchIdOf(await ReadJsonAsync(await ApplyAsync(session, TokenOf(preview))));

        // Two simultaneous undos serialize on the per-date advisory lock; the
        // winner reverts the batch, the loser re-checks "already undone" inside
        // its transaction and is reported as expired (410), never as a 409
        // conflict or an unhandled 500.
        var responses = await Task.WhenAll(
            UndoAsync(session, batchOperationId),
            UndoAsync(session, batchOperationId));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).Should().Be(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Gone).Should().Be(
            1,
            "the double-undo loser is expired, not a conflict");
        responses.Should().NotContain(response =>
            response.StatusCode == HttpStatusCode.InternalServerError);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntryHistory.CountAsync(
            row => row.ChangeReason == $"undo:{batchOperationId:D}", Ct))
            .Should().Be(changedDates.Length, "only the winner's undo rows are written");
    }

    [Fact]
    public async Task Undo_of_an_unknown_batch_returns_expired()
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
    public async Task Undo_of_a_batch_that_is_now_historical_returns_date_in_past()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        var clock = new TestTimeProvider(Now);
        using var factory = CreateFactory(clock);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today.AddDays(3), "seven_days"));
        var batchOperationId = BatchIdOf(await ReadJsonAsync(await ApplyAsync(session, TokenOf(preview))));

        // Move the household clock past the whole batch so every date is now
        // historical (past dates are immutable in normal use).
        clock.UtcNow = Now.AddDays(20);

        var response = await UndoAsync(session, batchOperationId);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntryHistory.CountAsync(
            row => row.ChangeReason == $"undo:{batchOperationId:D}", Ct)).Should().Be(0);
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
            $"{BatchOperationsPath}/not-a-guid/undo",
            json: null,
            session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").GetProperty("batch_operation_id")
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
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var editorSession = await SignInAsync(factory, "editor", MemberPassword))
        {
            var preview = await ReadJsonAsync(await PreviewAsync(editorSession, Today, "seven_days"));
            var apply = await ReadJsonAsync(await ApplyAsync(editorSession, TokenOf(preview)));
            var allowed = await UndoAsync(editorSession, BatchIdOf(apply));
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            var denied = await UndoBatchAsync(deniedSession.Client, Guid.NewGuid(), deniedSession.CsrfToken);
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorAsync(denied)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        using var anonymous = factory.CreateClient();
        (await UndoBatchAsync(anonymous, Guid.NewGuid(), csrfToken: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Undo_of_a_fill_empty_batch_restores_blank_dates()
    {
        await SeedAdminHouseholdAsync();
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");
        var occupied = Today.AddDays(2);
        await SeedEntryAsync(occupied, CalendarEntryType.Recipe, lasagna.Id, includeGroceries: true);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "fill_empty"));
        var changedDates = ProposedByDate(preview).Keys.OrderBy(date => date).ToArray();
        changedDates.Should().NotContain(occupied, "fill_empty never alters an existing entry");
        changedDates.Should().NotBeEmpty();

        var apply = await ReadJsonAsync(await ApplyAsync(session, TokenOf(preview)));
        var batchOperationId = BatchIdOf(apply);

        await using (var context = fixture.CreateContext())
        {
            var applyRows = await context.CalendarEntryHistory.AsNoTracking()
                .Where(row => row.BatchOperationId == batchOperationId)
                .ToListAsync(Ct);
            applyRows.Should().HaveCount(changedDates.Length);
            foreach (var row in applyRows)
            {
                using var document = JsonDocument.Parse(row.MetadataJson!);
                document.RootElement.GetProperty("kind").GetString().Should().Be("fill_empty");
            }
        }

        var response = await UndoAsync(session, batchOperationId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("reverted_count").GetInt32().Should().Be(changedDates.Length);
        foreach (var day in root.GetProperty("days").EnumerateArray())
        {
            day.GetProperty("entry").ValueKind.Should().Be(JsonValueKind.Null);
        }

        await using var verify = fixture.CreateContext();
        var stored = await verify.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Should().ContainSingle().Which.MealDate.Should().Be(occupied);
        (await verify.CalendarEntryHistory.CountAsync(
            row => row.ChangeReason == $"undo:{batchOperationId:D}", Ct)).Should().Be(changedDates.Length);
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

    private static Dictionary<DateOnly, long> AppliedByDate(JsonElement root)
    {
        var result = new Dictionary<DateOnly, long>();
        foreach (var day in root.GetProperty("days").EnumerateArray())
        {
            result[ParseDate(day)] = day.GetProperty("entry").GetProperty("recipe").GetProperty("id").GetInt64();
        }

        return result;
    }

    private static string TokenOf(JsonElement root) =>
        root.GetProperty("draft").GetProperty("token").GetString()!;

    private static Guid BatchIdOf(JsonElement root) =>
        Guid.Parse(root.GetProperty("operation").GetProperty("id").GetString()!);

    private static DateOnly ParseDate(JsonElement day) =>
        DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

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
                start_date = start.ToString("yyyy-MM-dd"),
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

    private static Task<HttpResponseMessage> UndoAsync(SignedInClient session, Guid batchOperationId) =>
        UndoBatchAsync(session.Client, batchOperationId, session.CsrfToken);

    private static Task<HttpResponseMessage> UndoBatchAsync(
        HttpClient client,
        Guid batchOperationId,
        string? csrfToken) =>
        SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{BatchOperationsPath}/{batchOperationId:D}/undo",
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
            HouseholdName = "Batch Undo Household",
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
