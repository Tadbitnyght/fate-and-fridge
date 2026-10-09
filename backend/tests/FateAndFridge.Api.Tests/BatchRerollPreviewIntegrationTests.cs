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
/// HTTP-level verification of the M7 batch reroll preview (docs/M7_PLAN.md
/// §1.3–§1.7, §2.2): ranges, past/horizon/mode/no-candidate failures, the
/// preserved-entry reasons, the sequential engine pass, draft TTL/supersede,
/// zero calendar writes, and the permission matrix. The clock is injected so
/// "today" is deterministic; the injected RNG is irrelevant because every valid
/// assertion holds regardless of which Normal recipe wins (the sequential-context
/// test uses a single candidate).
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class BatchRerollPreviewIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string PreviewPath = "/api/v1/calendar/reroll/preview";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "batch-reroll-preview-test";

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
    public async Task Seven_days_preview_ends_at_start_plus_six()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        var draft = root.GetProperty("draft");
        draft.GetProperty("mode").GetString().Should().Be("seven_days");
        draft.GetProperty("start_date").GetString().Should().Be("2026-11-15");
        draft.GetProperty("end_date").GetString().Should().Be("2026-11-21");
        draft.GetProperty("token").GetString().Should().NotBeNullOrWhiteSpace();

        var days = root.GetProperty("days");
        days.GetArrayLength().Should().Be(7);
        days.EnumerateArray().Last().GetProperty("date").GetString().Should().Be("2026-11-21");
    }

    [Fact]
    public async Task Rest_of_month_preview_ends_at_the_month_end()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "rest_of_month");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("draft").GetProperty("end_date").GetString().Should().Be("2026-11-30");
        var days = root.GetProperty("days");
        days.GetArrayLength().Should().Be(16);
        days.EnumerateArray().Last().GetProperty("date").GetString().Should().Be("2026-11-30");
    }

    [Fact]
    public async Task Preview_rejects_a_past_start_date()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today.AddDays(-1), "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);
    }

    [Fact]
    public async Task Preview_rejects_an_unknown_mode()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "weekly");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("mode", out _).Should().BeTrue("the mode field is flagged");
    }

    [Fact]
    public async Task Blank_and_plain_recipe_dates_are_proposed()
    {
        await SeedAdminHouseholdAsync();
        var existing = await SeedRecipeAsync("Lasagna");
        var other = await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(Today.AddDays(1), CalendarEntryType.Recipe, existing.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);

        var blank = DayFor(root, Today);
        blank.GetProperty("status").GetString().Should().Be("proposed");
        blank.GetProperty("unchanged_reason").ValueKind.Should().Be(JsonValueKind.Null);
        blank.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Null);
        blank.GetProperty("proposed").GetProperty("entry_type").GetString().Should().Be("recipe");
        blank.GetProperty("proposed").GetProperty("recipe_id").GetInt64()
            .Should().BeOneOf(existing.Id, other.Id);

        var filled = DayFor(root, Today.AddDays(1));
        filled.GetProperty("status").GetString().Should().Be("proposed");
        filled.GetProperty("current").GetProperty("entry_type").GetString().Should().Be("recipe");
        filled.GetProperty("current").GetProperty("id").GetInt64().Should().BePositive();
        filled.GetProperty("proposed").GetProperty("recipe_id").GetInt64()
            .Should().BeOneOf(existing.Id, other.Id);
    }

    [Fact]
    public async Task Locked_vacation_special_and_non_recipe_dates_are_preserved()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, recipe.Id, isLocked: true);
        await SeedEntryAsync(Today.AddDays(1), CalendarEntryType.Vacation, null);
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.Holiday, null);
        await SeedEntryAsync(Today.AddDays(3), CalendarEntryType.EatOut, null);
        await SeedEntryAsync(Today.AddDays(4), CalendarEntryType.FendForYourself, null);
        await SeedEntryAsync(Today.AddDays(6), CalendarEntryType.Misc, null);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);

        AssertUnchanged(root, Today, "locked");
        AssertUnchanged(root, Today.AddDays(1), "vacation");
        AssertUnchanged(root, Today.AddDays(2), "special");
        AssertUnchanged(root, Today.AddDays(3), "existing_non_recipe");
        AssertUnchanged(root, Today.AddDays(4), "existing_non_recipe");
        AssertUnchanged(root, Today.AddDays(6), "special");

        var blank = DayFor(root, Today.AddDays(5));
        blank.GetProperty("status").GetString().Should().Be("proposed");
    }

    [Fact]
    public async Task Preview_fails_when_no_normal_candidate_exists()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Manual", RecipeStatus.ManualOnly);
        await SeedRecipeAsync("Retired", RecipeStatus.Retired);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.RandomizerNoCandidates);
    }

    [Fact]
    public async Task Preview_writes_no_calendar_rows_and_creates_one_draft_with_a_fifteen_minute_ttl()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("draft").GetProperty("expires_at").GetDateTimeOffset()
            .Should().Be(Now.Add(BatchRerollService.PreviewTtl));

        await using var context = fixture.CreateContext();
        (await context.CalendarEntries.CountAsync(Ct)).Should().Be(0);
        (await context.CalendarEntryHistory.CountAsync(Ct)).Should().Be(0);

        var drafts = await context.CalendarRerollDrafts.AsNoTracking().ToListAsync(Ct);
        drafts.Should().ContainSingle();
        drafts[0].AppliedAt.Should().BeNull();
        drafts[0].CreatedAt.Should().Be(Now);
        drafts[0].ExpiresAt.Should().Be(Now.Add(BatchRerollService.PreviewTtl));
        drafts[0].Mode.Should().Be("seven_days");
        drafts[0].StartDate.Should().Be(Today);
        drafts[0].EndDate.Should().Be(Today.AddDays(6));
    }

    [Fact]
    public async Task A_second_preview_supersedes_the_first()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var first = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var firstToken = first.GetProperty("draft").GetProperty("token").GetString();
        var second = await ReadJsonAsync(await PreviewAsync(session, Today, "seven_days"));
        var secondToken = second.GetProperty("draft").GetProperty("token").GetString();

        secondToken.Should().NotBe(firstToken);

        await using var context = fixture.CreateContext();
        var drafts = await context.CalendarRerollDrafts.AsNoTracking().ToListAsync(Ct);
        drafts.Should().ContainSingle("Try Another discards the previous draft");
        drafts[0].Token.ToString().Should().Be(secondToken);
    }

    [Fact]
    public async Task Preview_rejects_a_start_date_beyond_the_horizon()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        // One year past the ±5-year planning horizon.
        var start = Today.AddYears(HouseholdClock.HorizonYears + 1);
        var response = await PreviewAsync(session, start, "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("start_date", out _).Should().BeTrue("the horizon cap flags the start date");
    }

    [Fact]
    public async Task Preview_sweeps_expired_drafts_of_any_actor_but_keeps_valid_ones()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var expiredToken = Guid.NewGuid();
        var validToken = Guid.NewGuid();
        await SeedDraftAsync(expiredToken, Now.AddMinutes(-1));
        await SeedDraftAsync(validToken, Now.AddMinutes(10));

        var response = await PreviewAsync(session, Today, "seven_days");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var context = fixture.CreateContext();
        var drafts = await context.CalendarRerollDrafts.AsNoTracking().ToListAsync(Ct);
        drafts.Select(draft => draft.Token).Should().NotContain(
            expiredToken,
            "an expired draft of any actor is swept");
        drafts.Select(draft => draft.Token).Should().Contain(
            validToken,
            "a still-valid draft of another actor is untouched");
        drafts.Should().HaveCount(2, "the new draft plus the still-valid one");
    }

    [Fact]
    public async Task Sequential_context_counts_an_earlier_pick_in_the_same_preview()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await PreviewAsync(session, Today, "seven_days");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        var firstId = DayFor(root, Today).GetProperty("proposed").GetProperty("recipe_id").GetInt64();
        var secondId = DayFor(root, Today.AddDays(1)).GetProperty("proposed").GetProperty("recipe_id").GetInt64();
        secondId.Should().Be(firstId, "the only Normal recipe wins every draw");

        await using var context = fixture.CreateContext();
        var draft = await context.CalendarRerollDrafts.AsNoTracking().SingleAsync(Ct);
        using var document = JsonDocument.Parse(draft.ProposalJson);
        var days = document.RootElement.GetProperty("days").EnumerateArray().ToArray();

        var firstMetadata = MetadataFor(days, Today);
        firstMetadata.GetProperty("kind").GetString().Should().Be("batch_reroll");
        firstMetadata.GetProperty("mode").GetString().Should().Be("seven_days");
        firstMetadata.GetProperty("repeat_occurrences").GetInt32().Should().Be(0);

        var secondMetadata = MetadataFor(days, Today.AddDays(1));
        secondMetadata.GetProperty("repeat_occurrences").GetInt32()
            .Should().Be(1, "the earlier pick in this preview is an occurrence for the next day");
        secondMetadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(0.10m);
    }

    [Fact]
    public async Task Preview_permission_matrix()
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
            var allowed = await PreviewAsync(editorSession, Today, "seven_days");
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            var denied = await PreviewAsync(deniedSession, Today.AddDays(1), "seven_days");
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorAsync(denied)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        using var anonymous = factory.CreateClient();
        (await PreviewAsync(anonymous, Today.AddDays(2), "seven_days", csrfToken: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    private static void AssertUnchanged(JsonElement root, DateOnly date, string reason)
    {
        var day = DayFor(root, date);
        day.GetProperty("status").GetString().Should().Be("unchanged");
        day.GetProperty("unchanged_reason").GetString().Should().Be(reason);
        day.GetProperty("proposed").ValueKind.Should().Be(JsonValueKind.Null);
        day.GetProperty("current").ValueKind.Should().Be(JsonValueKind.Object);
    }

    private static JsonElement DayFor(JsonElement root, DateOnly date)
    {
        var expected = date.ToString("yyyy-MM-dd");
        return root.GetProperty("days").EnumerateArray()
            .Single(day => day.GetProperty("date").GetString() == expected);
    }

    private static JsonElement MetadataFor(JsonElement[] days, DateOnly date)
    {
        var expected = date.ToString("yyyy-MM-dd");
        return days.Single(day => day.GetProperty("date").GetString() == expected)
            .GetProperty("metadata");
    }

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
        PreviewAsync(session.Client, start, mode, session.CsrfToken);

    private static Task<HttpResponseMessage> PreviewAsync(
        HttpClient client,
        DateOnly start,
        string mode,
        string? csrfToken) =>
        SendJsonAsync(
            client,
            HttpMethod.Post,
            PreviewPath,
            JsonSerializer.Serialize(new
            {
                start_date = start.ToString("yyyy-MM-dd"),
                mode,
            }),
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
            HouseholdName = "Batch Reroll Household",
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
        bool isLocked = false)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = type,
            RecipeId = recipeId,
            IsLocked = isLocked,
            IncludeGroceries = type == CalendarEntryType.Recipe,
            SnapshotDisplayText = type.ToString(),
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// Seeds a draft owned by the system actor (null user), so it is never swept
    /// as the signed-in actor's own unapplied draft — only the expiry rule can
    /// remove it.
    /// </summary>
    private async Task SeedDraftAsync(Guid token, DateTimeOffset expiresAt)
    {
        await using var context = fixture.CreateContext();
        context.CalendarRerollDrafts.Add(new CalendarRerollDraft
        {
            Token = token,
            Mode = BatchRerollService.ModeSevenDays,
            StartDate = Today,
            EndDate = Today.AddDays(6),
            ActorUserId = null,
            ProposalJson = "{}",
            CreatedAt = expiresAt - BatchRerollService.PreviewTtl,
            ExpiresAt = expiresAt,
            AppliedAt = null,
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
