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
/// HTTP-level verification of the M7 atomic batch apply (docs/M7_PLAN.md
/// §1.8–§1.12, §2.2): exact previewed recipes, per-date history grouped by one
/// batch operation id, conflict detection with full rollback, draft TTL/consumption,
/// per-actor isolation, the permission matrix, and the Try Another supersede
/// semantics of M7-02 (#96). The clock is injected so "today" is deterministic;
/// most assertions hold regardless of which Normal recipe wins, and the one
/// Try Another test that needs a specific draw injects a controlled
/// <see cref="IRandomSource"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class BatchApplyIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string PreviewPath = "/api/v1/calendar/reroll/preview";
    private const string ApplyPath = "/api/v1/calendar/reroll/apply";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "batch-apply-test";

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
    public async Task Apply_fills_blanks_and_replaces_a_recipe_with_the_previewed_recipes()
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
        var previewed = ProposedByDate(preview);
        previewed.Should().HaveCount(7, "every blank or plain-recipe date is proposed");

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("changed_count").GetInt32().Should().Be(previewed.Count);

        var operation = root.GetProperty("operation");
        Guid.TryParse(operation.GetProperty("id").GetString(), out var batchOperationId).Should().BeTrue();
        batchOperationId.Should().NotBeEmpty();
        operation.GetProperty("undo_expires_at").ValueKind.Should().Be(JsonValueKind.Null);

        var days = root.GetProperty("days").EnumerateArray().ToArray();
        days.Should().HaveCount(previewed.Count);
        foreach (var day in days)
        {
            var date = ParseDate(day);
            day.GetProperty("entry").GetProperty("entry_type").GetString().Should().Be("recipe");
            day.GetProperty("entry").GetProperty("recipe").GetProperty("id").GetInt64()
                .Should().Be(previewed[date], "Apply commits exactly the previewed recipe");
        }

        // The replaced entry is reset exactly like a single reroll.
        var replaced = days.Single(day => ParseDate(day) == Today.AddDays(1)).GetProperty("entry");
        replaced.GetProperty("diner_count").ValueKind.Should().Be(JsonValueKind.Null);
        replaced.GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
        replaced.GetProperty("restaurant_name").ValueKind.Should().Be(JsonValueKind.Null);
        replaced.GetProperty("location_text").ValueKind.Should().Be(JsonValueKind.Null);
        replaced.GetProperty("is_locked").GetBoolean().Should().BeFalse();
        replaced.GetProperty("include_groceries").GetBoolean().Should().BeTrue();

        // The stored rows match the response, and the blank dates now have entries.
        await using var context = fixture.CreateContext();
        var stored = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        stored.Should().HaveCount(previewed.Count);
        foreach (var (date, recipeId) in previewed)
        {
            stored.Single(entry => entry.MealDate == date).RecipeId.Should().Be(recipeId);
        }
    }

    [Fact]
    public async Task Apply_writes_one_history_row_per_changed_date_sharing_one_batch_operation_id()
    {
        await SeedAdminHouseholdAsync();
        var lasagna = await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.Recipe, lasagna.Id, includeGroceries: true);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var previewed = ProposedByDate(preview);
        var response = await ApplyAsync(session, TokenOf(preview));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        Guid.TryParse(
            (await ReadJsonAsync(response)).GetProperty("operation").GetProperty("id").GetString(),
            out var batchOperationId).Should().BeTrue();

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.AsNoTracking().ToListAsync(Ct);
        history.Should().HaveCount(previewed.Count, "exactly one row per changed date");
        history.Select(row => row.BatchOperationId).Distinct()
            .Should().ContainSingle("every row shares one batch operation id")
            .Which.Should().Be(batchOperationId);
        history.Select(row => row.OperationId).Distinct()
            .Should().HaveCount(previewed.Count, "each row keeps its own operation id");
        history.Should().OnlyContain(row => row.OperationId != null && row.OperationId != batchOperationId);

        foreach (var row in history)
        {
            row.MealDate.Should().BeOneOf(previewed.Keys);
            row.AfterJson.Should().NotBeNull();
            row.IsHistoricalCorrection.Should().BeFalse();
            if (row.MealDate == Today.AddDays(2))
            {
                row.BeforeJson.Should().NotBeNull("the replaced entry is snapshotted");
            }
            else
            {
                row.BeforeJson.Should().BeNull("a previously blank date has no prior document");
            }
        }
    }

    [Fact]
    public async Task Apply_writes_the_documented_metadata_json()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var response = await ApplyAsync(session, TokenOf(preview));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.AsNoTracking().ToListAsync(Ct);
        history.Should().NotBeEmpty();

        foreach (var row in history)
        {
            row.MetadataJson.Should().NotBeNull();
            using var document = JsonDocument.Parse(row.MetadataJson!);
            var metadata = document.RootElement;
            metadata.GetProperty("kind").GetString().Should().Be("batch_reroll");
            metadata.GetProperty("mode").GetString().Should().Be("seven_days");
            metadata.GetProperty("candidate_count").GetInt32().Should().Be(2);
            metadata.GetProperty("chosen_recipe_id").GetInt64().Should().BePositive();
            metadata.GetProperty("base_weight").GetDecimal().Should().Be(1.0m);
            metadata.TryGetProperty("final_weight", out _).Should().BeTrue();
            metadata.GetProperty("shared_proteins").ValueKind.Should().Be(JsonValueKind.Array);
        }
    }

    [Fact]
    public async Task Apply_conflict_on_a_later_date_writes_nothing()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));

        // A target date changes after the preview: a blank date gains an entry.
        await SeedEntryAsync(Today.AddDays(4), CalendarEntryType.EatOut, recipeId: null);

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchConflict);

        await using var context = fixture.CreateContext();
        // Atomic rollback: only the manually inserted conflicting entry exists;
        // no earlier proposed date was written.
        var entries = await context.CalendarEntries.AsNoTracking().ToListAsync(Ct);
        entries.Should().ContainSingle();
        entries[0].MealDate.Should().Be(Today.AddDays(4));
        (await context.CalendarEntryHistory.CountAsync(Ct)).Should().Be(0);

        var draft = await context.CalendarRerollDrafts.AsNoTracking().SingleAsync(Ct);
        draft.AppliedAt.Should().BeNull("a failed apply does not consume the draft");
    }

    [Fact]
    public async Task Apply_rejects_an_expired_draft()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");

        var clock = new TestTimeProvider(Now);
        using var factory = CreateFactory(clock);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        clock.Advance(TimeSpan.FromMinutes(16));

        var response = await ApplyAsync(session, TokenOf(preview));

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchDraftExpired);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Apply_rejects_an_already_applied_draft()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var token = TokenOf(preview);

        (await ApplyAsync(session, token)).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await ApplyAsync(session, token);

        second.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadErrorAsync(second)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchDraftExpired);
    }

    [Fact]
    public async Task Apply_rejects_a_target_date_that_became_historical_after_the_preview()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        // A valid preview is created while the whole range is editable.
        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var token = TokenOf(preview);

        // The household boundary advances past the previewed start date (e.g.
        // the household crossed local midnight), without aging the 15-minute
        // draft out. Apply must refuse to commit a now-past entry that could
        // never be undone.
        await SetHistoricalBoundaryAsync(Today);

        var response = await ApplyAsync(session, token);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.CountAsync(Ct)).Should().Be(0, "a now-past apply writes nothing");
        (await context.CalendarEntryHistory.CountAsync(Ct)).Should().Be(0);
        var draft = await context.CalendarRerollDrafts.AsNoTracking().SingleAsync(Ct);
        draft.AppliedAt.Should().BeNull("a rejected apply does not consume the draft");
    }

    [Fact]
    public async Task Apply_hides_another_actors_draft()
    {
        await SeedHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var editor = await SeedUserAsync("editor", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(editor.Id, PermissionKeys.CalendarEdit);
        await MarkSetupCompletedAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));

        string token;
        using (var adminSession = await SignInAsync(factory, "root", AdminPassword))
        {
            token = TokenOf(await ReadJsonAsync(await PreviewAsync(adminSession, Today, "seven_days")));
        }

        using (var editorSession = await SignInAsync(factory, "editor", MemberPassword))
        {
            var response = await ApplyAsync(editorSession, token);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ReadErrorAsync(response)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.NotFound);
        }

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.CountAsync(Ct)).Should().Be(0);
        var draft = await context.CalendarRerollDrafts.AsNoTracking().SingleAsync(Ct);
        draft.AppliedAt.Should().BeNull("another actor's draft is never consumed");
    }

    [Fact]
    public async Task Apply_rejects_a_malformed_token()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await ApplyAsync(session, "not-a-uuid");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("token", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_rejects_a_missing_token()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Post, ApplyPath, "{}", session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("token", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_permission_matrix()
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
            var preview = await ReadJsonAsync(await PreviewAsync(editorSession, Today, "seven_days"));
            var allowed = await ApplyAsync(editorSession, TokenOf(preview));
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            var denied = await ApplyAsync(deniedSession, Guid.NewGuid().ToString());
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorAsync(denied)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        using var anonymous = factory.CreateClient();
        (await ApplyAsync(anonymous, Guid.NewGuid().ToString(), csrfToken: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Apply_writes_no_audit_rows()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        (await ApplyAsync(session, TokenOf(preview))).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        (await context.AuditEvents.CountAsync(row => !row.EventType.StartsWith("auth."), Ct)).Should().Be(
            0,
            "a batch apply stages no audit row; M12-01 only mirrors auth events");
    }

    [Fact]
    public async Task Try_another_supersedes_the_first_draft_and_the_superseded_token_is_expired()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var first = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var firstToken = TokenOf(first);
        var second = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var secondToken = TokenOf(second);

        secondToken.Should().NotBe(firstToken, "Try Another returns a fresh token");

        await using (var context = fixture.CreateContext())
        {
            var drafts = await context.CalendarRerollDrafts.AsNoTracking().ToListAsync(Ct);
            drafts.Should().ContainSingle("Try Another discards the previous unapplied draft");
            drafts[0].Token.ToString().Should().Be(secondToken);
        }

        var superseded = await ApplyAsync(session, firstToken);
        superseded.StatusCode.Should().Be(HttpStatusCode.Gone);
        (await ReadErrorAsync(superseded)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.BatchDraftExpired);

        (await ApplyAsync(session, secondToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Try_another_can_yield_a_different_proposal()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");

        var randomSource = new FixedRandomSource { Value = 0.0m };
        using var factory = CreateFactory(new TestTimeProvider(Now), randomSource);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var first = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        randomSource.Value = 0.9m;
        var second = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));

        TokenOf(second).Should().NotBe(TokenOf(first));
        ProposedByDate(second)[Today].Should().NotBe(
            ProposedByDate(first)[Today],
            "with two Normal candidates a fresh draw can pick a different recipe");
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

    private static string TokenOf(JsonElement root) =>
        root.GetProperty("draft").GetProperty("token").GetString()!;

    private static DateOnly ParseDate(JsonElement day) =>
        DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private ApiFactory CreateFactory(TestTimeProvider clock, IRandomSource randomSource) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IRandomSource>();
            services.AddSingleton(randomSource);
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
        ApplyAsync(session.Client, token, session.CsrfToken);

    private static Task<HttpResponseMessage> ApplyAsync(
        HttpClient client,
        string token,
        string? csrfToken) =>
        SendJsonAsync(
            client,
            HttpMethod.Post,
            ApplyPath,
            JsonSerializer.Serialize(new { token }),
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
            HouseholdName = "Batch Apply Household",
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

    private async Task SetHistoricalBoundaryAsync(DateOnly boundary)
    {
        await using var context = fixture.CreateContext();
        var settings = await context.HouseholdSettings.SingleAsync(Ct);
        settings.HistoricalBoundaryDate = boundary;
        settings.UpdatedAt = SeedTimestamp;
        await context.SaveChangesAsync(Ct);
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

    /// <summary>Controlled <see cref="IRandomSource"/> so Try Another can force a distinct draw.</summary>
    private sealed class FixedRandomSource : IRandomSource
    {
        public decimal Value { get; set; }

        public decimal NextUnitInterval() => Value;
    }

    private sealed class SignedInClient(HttpClient client, CookieContainer cookies, string csrfToken)
        : IDisposable
    {
        public HttpClient Client { get; } = client;

        public CookieContainer Cookies { get; } = cookies;

        public string CsrfToken { get; } = csrfToken;

        public void Dispose() => Client.Dispose();
    }
}
