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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Issue #91: the exhaustive battery at the
/// endpoint/service level, complementing the deterministic engine matrix in
/// <see cref="RandomizerEngineTests"/>. These tests drive the real reroll route
/// against PostgreSQL and then assert the persisted <c>metadata_json</c>.
/// Selection is made deterministic by the single-Normal-candidate technique
/// when exactly one recipe has <c>status = Normal</c> the weighted draw must
/// return it, so the recorded score breakdown becomes a stable assertion of the
/// context the service built from the stored calendar. Helpers mirror
/// <see cref="RandomizerEndpointsIntegrationTests"/> (they are private there).
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class RandomizerMatrixIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string SettingsPath = "/api/v1/settings/household";
    private const string AdminPassword = "root horse battery staple";
    private const string TestUserAgent = "randomizer-matrix-test";

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

    /// <summary>
    /// docs/RANDOMIZER.md §4: a locked occurrence is still a planned occurrence
    /// so the only Normal recipe locked on D-1 must contribute the exact-repeat
    /// penalty when D is rerolled.
    /// </summary>
    [Fact]
    public async Task Locked_occurrence_on_the_previous_day_still_counts_for_the_repeat_penalty()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today.AddDays(-1), CalendarEntryType.Recipe, recipe.Id, isLocked: true);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("entry").GetProperty("recipe").GetProperty("id")
            .GetInt64().Should().Be(recipe.Id, "the only Normal recipe must win the single-candidate draw");

        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("candidate_count").GetInt32().Should().Be(1);
        metadata.GetProperty("repeat_occurrences").GetInt32().Should().Be(1);
        metadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(0.10m);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §5 : a special-day occurrence
    /// (holiday/host-family dinner/misc) contributes the special-day multiplier
    /// not the normal repeat multiplier.
    /// </summary>
    [Fact]
    public async Task Special_day_occurrence_uses_the_special_day_multiplier()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today.AddDays(-1), CalendarEntryType.Holiday, recipe.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("repeat_occurrences").GetInt32().Should().Be(1);
        metadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(0.02m);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §6 : any recipe-bearing neighbor
    /// counts for the protein penalty, including a ManualOnly entry that is never
    /// itself a candidate.
    /// </summary>
    [Fact]
    public async Task Shared_primary_protein_with_a_manual_only_neighbor_is_penalized_once()
    {
        await SeedAdminHouseholdAsync();
        var chicken = await SeedProteinAsync("Chicken");
        var candidate = await SeedRecipeAsync("Chicken Curry");
        await LinkProteinAsync(candidate.Id, chicken.Id);
        var manual = await SeedRecipeAsync("Manual Chicken", RecipeStatus.ManualOnly);
        await LinkProteinAsync(manual.Id, chicken.Id);
        await SeedEntryAsync(Today.AddDays(-1), CalendarEntryType.Recipe, manual.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("entry").GetProperty("recipe").GetProperty("id")
            .GetInt64().Should().Be(candidate.Id);
        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("protein_multiplier").GetDecimal().Should().Be(0.70m);
        metadata.GetProperty("shared_proteins").EnumerateArray()
            .Select(protein => protein.GetString()).Should().Equal("chicken");
    }

    /// <summary>
    /// docs/RANDOMIZER.md §6: unrelated proteins must not be penalized.
    /// </summary>
    [Fact]
    public async Task An_unrelated_protein_neighbor_does_not_penalize()
    {
        await SeedAdminHouseholdAsync();
        var chicken = await SeedProteinAsync("Chicken");
        var beef = await SeedProteinAsync("Beef");
        var candidate = await SeedRecipeAsync("Chicken Curry");
        await LinkProteinAsync(candidate.Id, chicken.Id);
        var manual = await SeedRecipeAsync("Manual Beef", RecipeStatus.ManualOnly);
        await LinkProteinAsync(manual.Id, beef.Id);
        await SeedEntryAsync(Today.AddDays(-1), CalendarEntryType.Recipe, manual.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("protein_multiplier").GetDecimal().Should().Be(1.0m);
        metadata.GetProperty("shared_proteins").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §7 Phase D: the nearest prior occurrence drives the
    /// spacing step (≤ near → 0.5, between → 0.75, ≥ far → 1.0) through the
    /// service-built context, using the default near=3 / far=8.
    /// </summary>
    [Theory]
    [InlineData(9, 1.0)]
    [InlineData(3, 0.5)]
    [InlineData(5, 0.75)]
    public async Task Spacing_uses_the_nearest_prior_occurrence(int offsetDays, double expectedMultiplier)
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today.AddDays(-offsetDays), CalendarEntryType.Recipe, recipe.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("spacing_days").GetInt32().Should().Be(offsetDays);
        metadata.GetProperty("spacing_multiplier").GetDecimal().Should().Be((decimal)expectedMultiplier);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §15 "Small Recipe Pool" : a pool
    /// entirely inside the repeat window still yields a result (never
    /// RANDOMIZER_NO_CANDIDATES).
    /// </summary>
    [Fact]
    public async Task Small_pool_single_candidate_inside_the_repeat_window_still_returns_a_result()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today.AddDays(-2), CalendarEntryType.Recipe, recipe.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("entry").GetProperty("recipe").GetProperty("id")
            .GetInt64().Should().Be(recipe.Id);
        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(0.10m);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §15 "Small Recipe Pool": with two Normal recipes that
    /// are both inside the repeat window, the reroll still returns one of them.
    /// </summary>
    [Fact]
    public async Task Small_pool_two_candidates_inside_the_repeat_window_still_returns_one()
    {
        await SeedAdminHouseholdAsync();
        var first = await SeedRecipeAsync("Lasagna");
        var second = await SeedRecipeAsync("Tacos");
        await SeedEntryAsync(Today.AddDays(-2), CalendarEntryType.Recipe, first.Id);
        await SeedEntryAsync(Today.AddDays(-3), CalendarEntryType.Recipe, second.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var chosenId = (await ReadJsonAsync(response)).GetProperty("entry").GetProperty("recipe")
            .GetProperty("id").GetInt64();
        chosenId.Should().BeOneOf(first.Id, second.Id);
        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(0.10m);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §15 "Future Plan": a future occurrence affects scoring
    /// (spacing) but is never modified by a single-date reroll outside its range.
    /// The occurrence sits on D+20 — outside the 14-day forward repeat window —
    /// so it must not contribute the repeat penalty yet must be byte-for-byte
    /// unchanged after the reroll.
    /// </summary>
    [Fact]
    public async Task Future_occurrence_affects_scoring_but_is_never_modified()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        var futureDate = Today.AddDays(20);
        await SeedEntryAsync(
            futureDate,
            CalendarEntryType.Recipe,
            recipe.Id,
            dinerCount: 3,
            notes: "do not touch");
        var before = await CaptureEntryAsync(futureDate);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await RerollAsync(session, Today);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var metadata = await ReadRerollMetadataAsync(Today);
        metadata.GetProperty("repeat_occurrences").GetInt32().Should().Be(0);
        metadata.GetProperty("repeat_multiplier").GetDecimal().Should().Be(1.0m);
        metadata.GetProperty("spacing_days").GetInt32().Should().Be(20);

        var after = await CaptureEntryAsync(futureDate);
        after.Should().Be(before, "a reroll must never alter a date outside its range");
    }

    /// <summary>
    /// "Timezone": the windows and the historical
    /// boundary use household-local dates via <see cref="HouseholdClock"/>. With
    /// the household on a zone whose local date differs from UTC around the
    /// boundary, the household's yesterday fails CALENDAR_DATE_IN_PAST while its
    /// today succeeds.
    /// </summary>
    [Theory]
    [InlineData("Pacific/Kiritimati", 0, 1)]
    [InlineData("Pacific/Midway", -1, 0)]
    public async Task Household_local_dates_drive_history_and_windows_across_the_utc_boundary(
        string timezone,
        int yesterdayOffset,
        int todayOffset)
    {
        await SeedAdminHouseholdAsync();
        await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var patch = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            SettingsPath,
            JsonSerializer.Serialize(new { timezone }),
            session.CsrfToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var past = await RerollAsync(session, Today.AddDays(yesterdayOffset));
        past.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(past)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);

        var current = await RerollAsync(session, Today.AddDays(todayOffset));
        current.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// docs/RANDOMIZER.md §15 "Randomness": repeated rerolls over the same data
    /// are not trivially identical. Production randomness is the OS-backed
    /// <see cref="SystemRandomSource"/> (contract asserted by
    /// <c>SystemRandomSourceTests</c>); this test also confirms the real host
    /// registers it, then samples the live route. With two equal-weight
    /// candidates the chance of 30 identical draws is ≈ 2·10⁻⁹, so the assertion
    /// is non-flaky in practice.
    /// </summary>
    [Fact]
    public async Task Repeated_rerolls_over_the_same_data_are_not_trivially_identical()
    {
        await SeedAdminHouseholdAsync();
        var first = await SeedRecipeAsync("Lasagna");
        var second = await SeedRecipeAsync("Tacos");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        factory.Services.GetRequiredService<IRandomSource>().Should().BeOfType<SystemRandomSource>();

        var chosen = new HashSet<long>();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var response = await RerollAsync(session, Today);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            chosen.Add((await ReadJsonAsync(response)).GetProperty("entry").GetProperty("recipe")
                .GetProperty("id").GetInt64());
        }

        chosen.Should().OnlyContain(id => id == first.Id || id == second.Id);
        chosen.Count.Should().BeGreaterThan(1);
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

    private async Task SeedHouseholdAsync(string timezone = "America/Chicago", int defaultDinerCount = 4)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Randomizer Matrix Household",
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

    private async Task<Protein> SeedProteinAsync(string name)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();

        // proteins are shared reference data: they are not cleared between tests
        // and the startup seeder inserts a default catalog, so reuse an existing
        // row instead of colliding with ix_proteins_normalized_name.
        var existing = await context.Proteins
            .AsNoTracking()
            .FirstOrDefaultAsync(protein => protein.NormalizedName == normalized.Normalized, Ct);
        if (existing is not null)
        {
            return existing;
        }

        var protein = new Protein
        {
            Name = normalized.Display,
            NormalizedName = normalized.Normalized,
            IsActive = true,
        };
        context.Proteins.Add(protein);
        await context.SaveChangesAsync(Ct);
        return protein;
    }

    private async Task LinkProteinAsync(long recipeId, long proteinId)
    {
        await using var context = fixture.CreateContext();
        context.RecipeProteins.Add(new RecipeProtein { RecipeId = recipeId, ProteinId = proteinId });
        await context.SaveChangesAsync(Ct);
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

    private async Task<JsonElement> ReadRerollMetadataAsync(DateOnly date)
    {
        await using var context = fixture.CreateContext();
        var row = await context.CalendarEntryHistory
            .AsNoTracking()
            .SingleAsync(history => history.MealDate == date && history.MetadataJson != null, Ct);
        using var document = JsonDocument.Parse(row.MetadataJson!);
        return document.RootElement.Clone();
    }

    private async Task<EntrySnapshot> CaptureEntryAsync(DateOnly date)
    {
        await using var context = fixture.CreateContext();
        var entry = await context.CalendarEntries
            .AsNoTracking()
            .SingleAsync(candidate => candidate.MealDate == date, Ct);
        return new EntrySnapshot(
            entry.Id,
            entry.MealDate,
            entry.EntryType,
            entry.RecipeId,
            entry.IsLocked,
            entry.DinerCount,
            entry.RestaurantName,
            entry.LocationText,
            entry.CustomLabel,
            entry.Notes,
            entry.IconKey,
            entry.IncludeGroceries,
            entry.SnapshotRecipeName,
            entry.SnapshotDisplayText,
            entry.CreatedByUserId,
            entry.UpdatedByUserId,
            entry.CreatedAt,
            entry.UpdatedAt);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error");

    /// <summary>Value-equal snapshot of every persisted calendar entry field.</summary>
    private sealed record EntrySnapshot(
        long Id,
        DateOnly MealDate,
        CalendarEntryType EntryType,
        long? RecipeId,
        bool IsLocked,
        int? DinerCount,
        string? RestaurantName,
        string? LocationText,
        string? CustomLabel,
        string? Notes,
        string? IconKey,
        bool IncludeGroceries,
        string? SnapshotRecipeName,
        string SnapshotDisplayText,
        long? CreatedByUserId,
        long? UpdatedByUserId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed class SignedInClient(HttpClient client, CookieContainer cookies, string csrfToken)
        : IDisposable
    {
        public HttpClient Client { get; } = client;

        public CookieContainer Cookies { get; } = cookies;

        public string CsrfToken { get; } = csrfToken;

        public void Dispose() => Client.Dispose();
    }
}
