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
/// HTTP-level verification of the M6 reroll route (docs/M6_PLAN.md §1.11,
/// docs/RANDOMIZER.md §13): blank fill, replacement with reset metadata, the
/// single atomic history row with metadata, historical/locked/vacation/
/// no-candidate failures, and the permission matrix. The clock is injected so
/// "today" is deterministic; the injected RNG is irrelevant because the valid
/// assertions never depend on which Normal recipe wins.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class RandomizerEndpointsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "randomizer-endpoints-test";

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
    public async Task Reroll_fills_a_blank_date_with_a_recipe_and_returns_the_operation()
    {
        await SeedAdminHouseholdAsync();
        var first = await SeedRecipeAsync("Lasagna");
        var second = await SeedRecipeAsync("Tacos");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var day = await ReadJsonAsync(response);
        day.GetProperty("date").GetString().Should().Be(Today.ToString("yyyy-MM-dd"));
        day.GetProperty("is_historical").GetBoolean().Should().BeFalse();

        var entry = day.GetProperty("entry");
        entry.GetProperty("entry_type").GetString().Should().Be("recipe");
        entry.GetProperty("include_groceries").GetBoolean().Should().BeTrue();
        entry.GetProperty("is_locked").GetBoolean().Should().BeFalse();
        var chosenId = entry.GetProperty("recipe").GetProperty("id").GetInt64();
        chosenId.Should().BeOneOf(first.Id, second.Id);

        var operation = day.GetProperty("operation");
        Guid.TryParse(operation.GetProperty("id").GetString(), out var operationId).Should().BeTrue();
        operationId.Should().NotBeEmpty();
        operation.GetProperty("undo_expires_at").ValueKind.Should().Be(JsonValueKind.Null);

        // Exactly one history row, both snapshots present, metadata populated.
        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.SingleAsync(Ct);
        history.OperationId.Should().Be(operationId);
        history.BeforeJson.Should().BeNull("a blank date has no prior document");
        history.AfterJson.Should().NotBeNull();
        history.IsHistoricalCorrection.Should().BeFalse();
    }

    [Fact]
    public async Task Reroll_replaces_an_existing_entry_and_resets_metadata()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(
            Today,
            CalendarEntryType.EatOut,
            recipeId: null,
            isLocked: false,
            dinerCount: 2,
            notes: "old note",
            includeGroceries: false);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await ReadJsonAsync(response)).GetProperty("entry");
        entry.GetProperty("entry_type").GetString().Should().Be("recipe");
        entry.GetProperty("recipe").GetProperty("id").GetInt64().Should().Be(recipe.Id);
        entry.GetProperty("diner_count").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("notes").ValueKind.Should().Be(JsonValueKind.Null);
        entry.GetProperty("is_locked").GetBoolean().Should().BeFalse();
        entry.GetProperty("include_groceries").GetBoolean().Should().BeTrue();

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.SingleAsync(Ct);
        history.BeforeJson.Should().NotBeNull("the prior entry is snapshotted");
        history.AfterJson.Should().NotBeNull();
    }

    [Fact]
    public async Task Reroll_writes_the_documented_metadata_json()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedRecipeAsync("Tacos");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var chosenId = (await ReadJsonAsync(response))
            .GetProperty("entry").GetProperty("recipe").GetProperty("id").GetInt64();

        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.SingleAsync(Ct);
        history.MetadataJson.Should().NotBeNull();

        using var document = JsonDocument.Parse(history.MetadataJson!);
        var metadata = document.RootElement;
        metadata.GetProperty("kind").GetString().Should().Be("reroll");
        metadata.GetProperty("candidate_count").GetInt32().Should().Be(2);
        metadata.GetProperty("chosen_recipe_id").GetInt64().Should().Be(chosenId);
        metadata.GetProperty("base_weight").GetDecimal().Should().Be(1.0m);
        metadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(1.0m);
        metadata.GetProperty("repeat_occurrences").GetInt32().Should().Be(0);
        metadata.GetProperty("protein_multiplier").GetDecimal().Should().Be(1.0m);
        metadata.GetProperty("shared_proteins").GetArrayLength().Should().Be(0);
        metadata.GetProperty("spacing_days").ValueKind.Should().Be(JsonValueKind.Null);
        metadata.GetProperty("spacing_multiplier").GetDecimal().Should().Be(1.0m);
        metadata.GetProperty("favorite_multiplier").GetDecimal().Should().Be(1.0m);
        metadata.GetProperty("final_weight").GetDecimal().Should().Be(1.0m);
    }

    [Fact]
    public async Task Reroll_rejects_a_historical_date()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today.AddDays(-1));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);
    }

    [Fact]
    public async Task Reroll_rejects_a_locked_date()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today, CalendarEntryType.Recipe, null, isLocked: true);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarEntryLocked);
    }

    [Fact]
    public async Task Reroll_rejects_a_vacation_date()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today, CalendarEntryType.Vacation, null);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.RandomizerNotApplicable);
    }

    [Fact]
    public async Task Reroll_fails_when_only_non_normal_recipes_exist()
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Manual", RecipeStatus.ManualOnly);
        await SeedRecipeAsync("Retired", RecipeStatus.Retired);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.RandomizerNoCandidates);
    }

    [Fact]
    public async Task Reroll_permission_matrix()
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
            var allowed = await RerollAsync(editorSession, Today);
            allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            var denied = await RerollAsync(deniedSession, Today.AddDays(1));
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ReadErrorAsync(denied)).GetProperty("code").GetString()
                .Should().Be(ErrorCodes.Forbidden);
        }

        using var anonymous = factory.CreateClient();
        (await RerollAsync(anonymous, Today.AddDays(2), csrfToken: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private static Task<HttpResponseMessage> RerollAsync(
        SignedInClient session,
        DateOnly date) =>
        SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{date:yyyy-MM-dd}/reroll",
            json: null,
            session.CsrfToken);

    private static Task<HttpResponseMessage> RerollAsync(
        HttpClient client,
        DateOnly date,
        string? csrfToken) =>
        SendJsonAsync(
            client,
            HttpMethod.Post,
            $"{CalendarPath}/{date:yyyy-MM-dd}/reroll",
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
            HouseholdName = "Randomizer Household",
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
        bool includeGroceries = false)
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
