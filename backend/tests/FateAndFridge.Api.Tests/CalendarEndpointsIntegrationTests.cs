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
/// HTTP-level verification of the M5 calendar core routes
/// reads/writes, per-type validation, snapshots, lock semantics, the frozen
/// historical boundary, range/horizon caps, the repeat check, and the full
/// permission matrix. The clock is injected so "today" is deterministic.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class CalendarEndpointsIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "calendar-endpoints-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago is the same calendar date. Kept comfortably
    // after the real host date so the injected clock does not make the session
    // cookie (which carries a real expiry) look already expired to the cookie jar.
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
    public async Task Blank_date_reads_return_null_with_editability()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.Client.GetAsync($"{CalendarPath}/{Today:yyyy-MM-dd}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("entry").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("is_today").GetBoolean().Should().BeTrue();
        root.GetProperty("can_edit").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Put_creates_a_recipe_entry_and_reads_back_with_summary()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var put = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipe.Id }),
            session.CsrfToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var day = await ReadJsonAsync(put);
        var entry = day.GetProperty("entry");
        entry.GetProperty("entry_type").GetString().Should().Be("recipe");
        entry.GetProperty("display_text").GetString().Should().Be("Lasagna");
        entry.GetProperty("include_groceries").GetBoolean().Should().BeTrue();
        entry.GetProperty("recipe").GetProperty("id").GetInt64().Should().Be(recipe.Id);

        var get = await session.Client.GetAsync($"{CalendarPath}/{Today:yyyy-MM-dd}", Ct);
        (await ReadJsonAsync(get)).GetProperty("entry").GetProperty("id").GetInt64()
            .Should().Be(entry.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Put_is_a_full_replace_upsert()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            """{"entry_type":"eat_out","restaurant_name":"Pizza Place"}""", session.CsrfToken);
        var second = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            """{"entry_type":"fend_for_yourself"}""", session.CsrfToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var entry = (await ReadJsonAsync(second)).GetProperty("entry");
        entry.GetProperty("entry_type").GetString().Should().Be("fend_for_yourself");
        entry.GetProperty("restaurant_name").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Misc_requires_a_custom_label()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            """{"entry_type":"misc"}""", session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarMiscLabelRequired);
    }

    [Fact]
    public async Task Recipe_type_requires_a_recipe()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            """{"entry_type":"recipe"}""", session.CsrfToken);

        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarRecipeRequired);
    }

    [Fact]
    public async Task Unsupported_recipe_id_is_a_generic_validation_failure()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        // An Eat out entry cannot carry a recipe; the failure is a plain
        // validation error, not the "choose a recipe" contract (BUG-004).
        var response = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            JsonSerializer.Serialize(new { entry_type = "eat_out", recipe_id = recipe.Id }),
            session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors").TryGetProperty("entry_type", out _)
            .Should().BeTrue();

        // The same recipe_id on a type that does support it is accepted.
        var supported = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            JsonSerializer.Serialize(new { entry_type = "holiday", recipe_id = recipe.Id }),
            session.CsrfToken);
        supported.StatusCode.Should().Be(HttpStatusCode.OK);
    }


    [Fact]
    public async Task Diner_count_and_icon_validation_are_enforced()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = $"{CalendarPath}/{Today:yyyy-MM-dd}";

        var badDiner = await SendJsonAsync(
            session.Client, HttpMethod.Put, date,
            """{"entry_type":"host_family_dinner","diner_count":0}""", session.CsrfToken);
        badDiner.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A recipe entry cannot carry an icon.
        var badIcon = await SendJsonAsync(
            session.Client, HttpMethod.Put, date,
            JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipe.Id, icon_key = "star" }),
            session.CsrfToken);
        (await ReadErrorAsync(badIcon)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ValidationFailed);

        var okIcon = await SendJsonAsync(
            session.Client, HttpMethod.Put, date,
            """{"entry_type":"holiday","custom_label":"Thanksgiving","icon_key":"turkey"}""", session.CsrfToken);
        okIcon.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Locking_a_blank_date_fails_and_locking_an_entry_succeeds()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = $"{CalendarPath}/{Today:yyyy-MM-dd}";

        var blank = await SendJsonAsync(session.Client, HttpMethod.Post, $"{date}/lock", null, session.CsrfToken);
        blank.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(blank)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarBlankCannotLock);

        await SendJsonAsync(session.Client, HttpMethod.Put, date, """{"entry_type":"eat_out"}""", session.CsrfToken);
        var locked = await SendJsonAsync(session.Client, HttpMethod.Post, $"{date}/lock", null, session.CsrfToken);
        locked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(locked)).GetProperty("entry").GetProperty("is_locked").GetBoolean()
            .Should().BeTrue();

        // A manual edit keeps the lock (locks restrict automatic mutation only).
        var edited = await SendJsonAsync(
            session.Client, HttpMethod.Put, date,
            """{"entry_type":"eat_out","restaurant_name":"New Place"}""", session.CsrfToken);
        (await ReadJsonAsync(edited)).GetProperty("entry").GetProperty("is_locked").GetBoolean()
            .Should().BeTrue();
    }

    [Fact]
    public async Task Past_dates_reject_every_write_for_admin()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var yesterday = Today.AddDays(-1).ToString("yyyy-MM-dd");

        var put = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{yesterday}",
            """{"entry_type":"vacation"}""", session.CsrfToken);
        put.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(put)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);

        var lockResponse = await SendJsonAsync(
            session.Client, HttpMethod.Post, $"{CalendarPath}/{yesterday}/lock", null, session.CsrfToken);
        lockResponse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var get = await session.Client.GetAsync($"{CalendarPath}/{yesterday}", Ct);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(get)).GetProperty("is_historical").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Delete_removes_a_future_entry_and_writes_history()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = $"{CalendarPath}/{Today:yyyy-MM-dd}";

        await SendJsonAsync(session.Client, HttpMethod.Put, date, """{"entry_type":"vacation"}""", session.CsrfToken);
        var deleted = await SendJsonAsync(session.Client, HttpMethod.Delete, date, null, session.CsrfToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var read = await session.Client.GetAsync(date, Ct);
        (await ReadJsonAsync(read)).GetProperty("entry").ValueKind.Should().Be(JsonValueKind.Null);

        await using var context = fixture.CreateContext();
        var historyCount = await context.CalendarEntryHistory.CountAsync(Ct);
        historyCount.Should().BeGreaterThanOrEqualTo(2); // create + delete
    }

    [Fact]
    public async Task Vacation_exposes_the_blocked_automation_flag()
    {
        await SeedAdminHouseholdAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
            """{"entry_type":"vacation"}""", session.CsrfToken);

        var entry = (await ReadJsonAsync(response)).GetProperty("entry");
        entry.GetProperty("blocks_automation").GetBoolean().Should().BeTrue();
        entry.GetProperty("include_groceries").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Effective_diner_count_falls_back_to_the_household_default()
    {
        await SeedAdminHouseholdAsync(defaultDinerCount: 6);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = $"{CalendarPath}/{Today:yyyy-MM-dd}";

        var withDefault = await SendJsonAsync(
            session.Client, HttpMethod.Put, date, """{"entry_type":"eat_out"}""", session.CsrfToken);
        var defaultEntry = (await ReadJsonAsync(withDefault)).GetProperty("entry");
        defaultEntry.GetProperty("diner_count").ValueKind.Should().Be(JsonValueKind.Null);
        defaultEntry.GetProperty("effective_diner_count").GetInt32().Should().Be(6);

        var withOverride = await SendJsonAsync(
            session.Client, HttpMethod.Put, date,
            """{"entry_type":"eat_out","diner_count":2}""", session.CsrfToken);
        (await ReadJsonAsync(withOverride)).GetProperty("entry")
            .GetProperty("effective_diner_count").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Repeat_check_uses_the_seven_back_fourteen_forward_window()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(Today.AddDays(-7), CalendarEntryType.Recipe, recipe.Id);
        await SeedEntryAsync(Today.AddDays(14), CalendarEntryType.Recipe, recipe.Id);
        await SeedEntryAsync(Today.AddDays(-8), CalendarEntryType.Recipe, recipe.Id);
        await SeedEntryAsync(Today.AddDays(15), CalendarEntryType.Recipe, recipe.Id);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.Client.GetAsync(
            $"{CalendarPath}/{Today:yyyy-MM-dd}/repeat-check?recipe_id={recipe.Id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("is_repeat").GetBoolean().Should().BeTrue();
        var dates = root.GetProperty("occurrences").EnumerateArray()
            .Select(item => item.GetProperty("date").GetString()).ToArray();
        dates.Should().Equal(
            Today.AddDays(-7).ToString("yyyy-MM-dd"),
            Today.AddDays(14).ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task Historical_display_is_frozen_while_future_display_tracks_renames()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        var clock = new TestTimeProvider(Now);
        using var factory = CreateFactory(clock);
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var date = $"{CalendarPath}/{Today:yyyy-MM-dd}";

        await SendJsonAsync(
            session.Client, HttpMethod.Put, date,
            JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipe.Id }),
            session.CsrfToken);

        // Rename directly and advance past the entry: the historical display
        // must keep the frozen snapshot.
        await RenameRecipeAsync(recipe.Id, "Lasagna Deluxe");
        clock.Advance(TimeSpan.FromDays(1));

        var historical = await session.Client.GetAsync(date, Ct);
        var historicalDay = await ReadJsonAsync(historical);
        historicalDay.GetProperty("is_historical").GetBoolean().Should().BeTrue();
        historicalDay.GetProperty("can_edit").GetBoolean().Should().BeFalse();
        historicalDay.GetProperty("entry").GetProperty("display_text").GetString()
            .Should().Be("Lasagna");
    }

    [Fact]
    public async Task Future_entry_display_tracks_a_rename()
    {
        await SeedAdminHouseholdAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var tomorrow = Today.AddDays(1);
        var date = $"{CalendarPath}/{tomorrow:yyyy-MM-dd}";

        await SendJsonAsync(
            session.Client, HttpMethod.Put, date,
            JsonSerializer.Serialize(new { entry_type = "recipe", recipe_id = recipe.Id }),
            session.CsrfToken);
        await RenameRecipeAsync(recipe.Id, "Lasagna Deluxe");

        var read = await session.Client.GetAsync(date, Ct);
        (await ReadJsonAsync(read)).GetProperty("entry").GetProperty("display_text").GetString()
            .Should().Be("Lasagna Deluxe");
    }

    [Fact]
    public async Task Range_reads_return_only_non_blank_days_and_enforce_caps()
    {
        await SeedAdminHouseholdAsync();
        await SeedEntryAsync(Today, CalendarEntryType.Vacation, null);
        await SeedEntryAsync(Today.AddDays(2), CalendarEntryType.EatOut, null);
        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.Client.GetAsync(
            $"{CalendarPath}?start={Today:yyyy-MM-dd}&end={Today.AddDays(3):yyyy-MM-dd}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = await ReadJsonAsync(response);
        root.GetProperty("week_starts_on").GetInt32().Should().Be(0);
        root.GetProperty("entries").GetArrayLength().Should().Be(2);

        var tooLong = await session.Client.GetAsync(
            $"{CalendarPath}?start={Today:yyyy-MM-dd}&end={Today.AddDays(367):yyyy-MM-dd}", Ct);
        tooLong.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var outOfHorizon = await session.Client.GetAsync(
            $"{CalendarPath}?start={Today:yyyy-MM-dd}&end={Today.AddYears(6):yyyy-MM-dd}", Ct);
        outOfHorizon.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Permission_matrix_gates_reads_and_writes()
    {
        await SeedHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var viewer = await SeedUserAsync("viewer", MemberPassword, UserRole.Member);
        await SeedPermissionAsync(viewer.Id, PermissionKeys.CalendarView);
        await SeedUserAsync("nobody", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();
        using var factory = CreateFactory(new TestTimeProvider(Now));

        using (var viewerSession = await SignInAsync(factory, "viewer", MemberPassword))
        {
            var read = await viewerSession.Client.GetAsync($"{CalendarPath}/{Today:yyyy-MM-dd}", Ct);
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            var write = await SendJsonAsync(
                viewerSession.Client, HttpMethod.Put, $"{CalendarPath}/{Today:yyyy-MM-dd}",
                """{"entry_type":"eat_out"}""", viewerSession.CsrfToken);
            write.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var deniedSession = await SignInAsync(factory, "nobody", MemberPassword))
        {
            var read = await deniedSession.Client.GetAsync($"{CalendarPath}/{Today:yyyy-MM-dd}", Ct);
            read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync($"{CalendarPath}/{Today:yyyy-MM-dd}", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Frozen_boundary_keeps_history_historical_after_a_timezone_change()
    {
        // Start far east so "today" is the 16th, then move west so the same
        // instant becomes the 15th. The 15th was historical before the change
        // and must stay historical (read-only) rather than become editable.
        await SeedHouseholdAsync(timezone: "Pacific/Kiritimati");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var factory = CreateFactory(clock);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        // Under Kiritimati the 15th is yesterday (historical) and the 16th is today.
        var before = await ReadJsonAsync(await session.Client.GetAsync($"{CalendarPath}/2026-11-15", Ct));
        before.GetProperty("is_historical").GetBoolean().Should().BeTrue("the 15th is yesterday");
        var beforeToday = await ReadJsonAsync(await session.Client.GetAsync($"{CalendarPath}/2026-11-16", Ct));
        beforeToday.GetProperty("is_today").GetBoolean().Should().BeTrue();

        var patch = await SendJsonAsync(
            session.Client, HttpMethod.Patch, "/api/v1/settings/household",
            """{"timezone":"Pacific/Honolulu"}""", session.CsrfToken);
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        // The 15th is now "today" but was historical, so the freeze keeps it
        // read-only instead of making it editable again.
        var day = await ReadJsonAsync(await session.Client.GetAsync($"{CalendarPath}/2026-11-15", Ct));
        day.GetProperty("is_today").GetBoolean().Should().BeTrue();
        day.GetProperty("is_historical").GetBoolean().Should().BeTrue();
        day.GetProperty("can_edit").GetBoolean().Should().BeFalse();

        var write = await SendJsonAsync(
            session.Client, HttpMethod.Put, $"{CalendarPath}/2026-11-15",
            """{"entry_type":"vacation"}""", session.CsrfToken);
        write.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(write)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);
    }

    [Fact]
    public async Task A_recipe_rename_never_touches_frozen_history_after_a_timezone_change()
    {
        // Kiritimati "today" is the 16th; the 15th is historical. Move west so
        // the 15th becomes the current day, then rename the recipe through the
        // API — the frozen snapshot must not change.
        await SeedHouseholdAsync(timezone: "Pacific/Kiritimati");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(new DateOnly(2026, 11, 15), CalendarEntryType.Recipe, recipe.Id, "Lasagna");

        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var factory = CreateFactory(clock);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var patchSettings = await SendJsonAsync(
            session.Client, HttpMethod.Patch, "/api/v1/settings/household",
            """{"timezone":"Pacific/Honolulu"}""", session.CsrfToken);
        patchSettings.StatusCode.Should().Be(HttpStatusCode.OK);

        var rename = await SendJsonAsync(
            session.Client, HttpMethod.Patch, $"/api/v1/recipes/{recipe.Id}",
            """{"name":"Lasagna Deluxe"}""", session.CsrfToken);
        rename.StatusCode.Should().Be(HttpStatusCode.OK);

        var day = await ReadJsonAsync(await session.Client.GetAsync($"{CalendarPath}/2026-11-15", Ct));
        day.GetProperty("is_historical").GetBoolean().Should().BeTrue();
        day.GetProperty("entry").GetProperty("display_text").GetString().Should().Be("Lasagna");
    }

    private ApiFactory CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

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
            HouseholdName = "Calendar Household",
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

    private async Task RenameRecipeAsync(long recipeId, string name)
    {
        var normalized = (ReferenceDataNormalizationResult.Success)ReferenceDataNormalizer.Normalize(name)!;
        await using var context = fixture.CreateContext();
        var recipe = await context.Recipes.SingleAsync(candidate => candidate.Id == recipeId, Ct);
        recipe.Name = normalized.Display;
        recipe.NormalizedName = normalized.Normalized;
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedEntryAsync(
        DateOnly date,
        CalendarEntryType type,
        long? recipeId,
        string? displayText = null)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = type,
            RecipeId = recipeId,
            SnapshotDisplayText = displayText ?? type.ToString(),
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
