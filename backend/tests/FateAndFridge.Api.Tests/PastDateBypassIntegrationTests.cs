using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
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
/// Past-date bypass battery. Every mutation path is probed
/// with a past household-local date: <c>PUT</c>/<c>DELETE</c>, lock/unlock
/// single-date reroll, batch preview/apply, single and batch undo, and the
/// Admin correction route. Normal paths must fail <c>422 CALENDAR_DATE_IN_PAST</c>
/// for a member holding <c>calendar.edit</c> (and for an Admin — the normal
/// endpoints never accept history); the correction route is the only allowed
/// past-date writer and still requires the reason and typed date confirmation.
/// The frozen-timezone-boundary interaction is covered for every write verb: a
/// date that was historical stays immutable when a timezone change makes it the
/// current local day. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SetupCollection.Name)]
public sealed class PastDateBypassIntegrationTests(SetupFixture fixture) : IAsyncLifetime
{
    private const string CalendarPath = "/api/v1/calendar";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string Reason = "Correcting a historical record after a data-entry mistake";

    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Mid-day UTC so America/Chicago (UTC-6) is the same calendar date.
    private static readonly DateTimeOffset Now =
        new(2026, 11, 15, 18, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 11, 15);
    private static readonly DateOnly PastDate = new(2026, 11, 10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Member_with_calendar_edit_cannot_mutate_a_past_date_on_any_normal_path()
    {
        await SeedHouseholdAsync("America/Chicago");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var member = await SeedUserAsync("editor", MemberPassword, UserRole.Member);
        await GrantAsync(member.Id, PermissionKeys.CalendarEdit);
        await MarkSetupCompletedAsync();
        var recipe = await SeedRecipeAsync("Lasagna");
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "editor", MemberPassword);
        var past = PastDate.ToString("yyyy-MM-dd");

        var put = await SendJsonAsync(
            session.Client,
            HttpMethod.Put,
            $"{CalendarPath}/{past}",
            """{"entry_type":"vacation"}""",
            session.CsrfToken);
        await AssertPastAsync(put);

        var delete = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{CalendarPath}/{past}",
            json: null,
            session.CsrfToken);
        await AssertPastAsync(delete);

        var lockResponse = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{past}/lock",
            json: null,
            session.CsrfToken);
        await AssertPastAsync(lockResponse);

        var unlock = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{past}/unlock",
            json: null,
            session.CsrfToken);
        await AssertPastAsync(unlock);

        var reroll = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{past}/reroll",
            json: null,
            session.CsrfToken);
        await AssertPastAsync(reroll);

        var preview = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            JsonSerializer.Serialize(new { start_date = past, mode = "seven_days" }),
            session.CsrfToken);
        await AssertPastAsync(preview);

        // The repeat check is a read, not a mutation: it remains available for
        // a past date (no bypass, no write).
        var repeatCheck = await session.Client.GetAsync(
            $"{CalendarPath}/{past}/repeat-check?recipe_id={recipe.Id}",
            Ct);
        repeatCheck.StatusCode.Should().Be(HttpStatusCode.OK);

        // The only past-date write path is Admin-only.
        var correction = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{past}/correction",
            CorrectionJson("""{"entry_type":"eat_out"}""", Reason, past),
            session.CsrfToken);
        correction.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadErrorAsync(correction)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.Forbidden);

        // Nothing changed: the seeded entry and the history table are intact.
        await using var context = fixture.CreateContext();
        var entry = await context.CalendarEntries.AsNoTracking().SingleAsync(Ct);
        entry.MealDate.Should().Be(PastDate);
        entry.EntryType.Should().Be(CalendarEntryType.EatOut);
        entry.IsLocked.Should().BeFalse();
        (await context.CalendarEntryHistory.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task An_admin_also_cannot_mutate_a_past_date_through_the_normal_paths()
    {
        await SeedHouseholdAsync("America/Chicago");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var past = PastDate.ToString("yyyy-MM-dd");

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Put, $"{CalendarPath}/{past}", """{"entry_type":"vacation"}"""),
            (HttpMethod.Delete, $"{CalendarPath}/{past}", null),
            (HttpMethod.Post, $"{CalendarPath}/{past}/lock", null),
            (HttpMethod.Post, $"{CalendarPath}/{past}/unlock", null),
            (HttpMethod.Post, $"{CalendarPath}/{past}/reroll", null),
        })
        {
            var response = await SendJsonAsync(session.Client, method, path, body, session.CsrfToken);
            await AssertPastAsync(response);
        }

        await using var context = fixture.CreateContext();
        (await context.CalendarEntryHistory.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task Single_date_undo_fails_closed_after_the_rerolled_date_becomes_historical()
    {
        await SeedHouseholdAsync("America/Chicago");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        await SeedRecipeAsync("Lasagna");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var reroll = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{Today:yyyy-MM-dd}/reroll",
            json: null,
            session.CsrfToken);
        reroll.StatusCode.Should().Be(HttpStatusCode.OK);
        var operationId = (await ReadJsonAsync(reroll))
            .GetProperty("operation").GetProperty("id").GetString()!;

        // A timezone change moves the household past the rerolled date without
        // advancing the clock; the operation handle is still alive (undo has no
        // TTL), so only the historical check can reject it.
        await ShiftTimezoneAsync(session, "Pacific/Kiritimati");

        var undo = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"/api/v1/operations/{operationId}/undo",
            json: null,
            session.CsrfToken);
        await AssertPastAsync(undo);
    }

    [Fact]
    public async Task Batch_apply_and_batch_undo_fail_closed_when_the_target_becomes_historical()
    {
        await SeedHouseholdAsync("America/Chicago");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        await SeedRecipeAsync("Lasagna");

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var preview = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/preview",
            JsonSerializer.Serialize(new { start_date = Today.ToString("yyyy-MM-dd"), mode = "seven_days" }),
            session.CsrfToken);
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await ReadJsonAsync(preview))
            .GetProperty("draft").GetProperty("token").GetString()!;

        var apply = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/reroll/apply",
            JsonSerializer.Serialize(new { token }),
            session.CsrfToken);
        apply.StatusCode.Should().Be(HttpStatusCode.OK);
        var batchOperationId = (await ReadJsonAsync(apply))
            .GetProperty("operation").GetProperty("id").GetString()!;

        await ShiftTimezoneAsync(session, "Pacific/Kiritimati");

        var batchUndo = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"/api/v1/batch-operations/{batchOperationId}/undo",
            json: null,
            session.CsrfToken);
        await AssertPastAsync(batchUndo);
    }

    [Fact]
    public async Task Frozen_boundary_blocks_every_write_verb_after_a_timezone_change()
    {
        // Kiritimati is already on the 16th, so the 15th was historical.
        await SeedHouseholdAsync("Pacific/Kiritimati");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        var member = await SeedUserAsync("editor", MemberPassword, UserRole.Member);
        await GrantAsync(member.Id, PermissionKeys.CalendarEdit);
        await MarkSetupCompletedAsync();
        await SeedEntryAsync(new DateOnly(2026, 11, 15), CalendarEntryType.EatOut);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var adminSession = await SignInAsync(factory, "root", AdminPassword);

        // Move west so the 15th becomes the current local day. The frozen
        // boundary (14th) keeps the 15th historical.
        await ShiftTimezoneAsync(adminSession, "Pacific/Honolulu");

        using var session = await SignInAsync(factory, "editor", MemberPassword);
        var frozen = new DateOnly(2026, 11, 15).ToString("yyyy-MM-dd");

        var day = await ReadJsonAsync(
            await session.Client.GetAsync($"{CalendarPath}/{frozen}", Ct));
        day.GetProperty("is_today").GetBoolean().Should().BeTrue();
        day.GetProperty("is_historical").GetBoolean().Should().BeTrue();
        day.GetProperty("can_edit").GetBoolean().Should().BeFalse();

        foreach (var (method, path, body) in new (HttpMethod, string, string?)[]
        {
            (HttpMethod.Put, $"{CalendarPath}/{frozen}", """{"entry_type":"vacation"}"""),
            (HttpMethod.Delete, $"{CalendarPath}/{frozen}", null),
            (HttpMethod.Post, $"{CalendarPath}/{frozen}/lock", null),
            (HttpMethod.Post, $"{CalendarPath}/{frozen}/unlock", null),
            (HttpMethod.Post, $"{CalendarPath}/{frozen}/reroll", null),
        })
        {
            var response = await SendJsonAsync(session.Client, method, path, body, session.CsrfToken);
            await AssertPastAsync(response);
        }
    }

    [Fact]
    public async Task Admin_correction_is_the_only_past_date_writer_and_requires_reason_and_confirmation()
    {
        await SeedHouseholdAsync("America/Chicago");
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();
        await SeedEntryAsync(PastDate, CalendarEntryType.EatOut);

        using var factory = CreateFactory(new TestTimeProvider(Now));
        using var session = await SignInAsync(factory, "root", AdminPassword);
        var past = PastDate.ToString("yyyy-MM-dd");

        // Missing reason.
        var noReason = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{past}/correction",
            CorrectionJson("""{"entry_type":"vacation"}""", reason: null, past),
            session.CsrfToken);
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(noReason)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ReasonRequired);

        // Wrong typed confirmation (today's date instead of the corrected date).
        var wrongConfirmation = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{past}/correction",
            CorrectionJson("""{"entry_type":"vacation"}""", Reason, Today.ToString("yyyy-MM-dd")),
            session.CsrfToken);
        wrongConfirmation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(wrongConfirmation)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.ConfirmationMismatch);

        // The exact date plus a reason corrects the entry.
        var accepted = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            $"{CalendarPath}/{past}/correction",
            CorrectionJson("""{"entry_type":"vacation"}""", Reason, past),
            session.CsrfToken);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(accepted)).GetProperty("entry").GetProperty("entry_type").GetString()
            .Should().Be("vacation");

        // The correction is the only path that wrote history.
        await using var context = fixture.CreateContext();
        var history = await context.CalendarEntryHistory.AsNoTracking().SingleAsync(Ct);
        history.MealDate.Should().Be(PastDate);
        history.IsHistoricalCorrection.Should().BeTrue();
        history.ChangeReason.Should().Be(Reason);
    }

    private async Task ShiftTimezoneAsync(SignedInClient session, string timezone)
    {
        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Patch,
            "/api/v1/settings/household",
            JsonSerializer.Serialize(new { timezone }),
            session.CsrfToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static string CorrectionJson(string fields, string? reason, string? confirmation)
    {
        using var document = JsonDocument.Parse(fields);
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            body[property.Name] = property.Value.Clone();
        }

        body["reason"] = reason;
        body["confirmation"] = confirmation;
        return JsonSerializer.Serialize(body);
    }

    private static async Task AssertPastAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadErrorAsync(response)).GetProperty("code").GetString()
            .Should().Be(ErrorCodes.CalendarDateInPast);
    }

    private static async Task<JsonElement> ReadErrorAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
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

    private WebApplicationFactory<Program> CreateFactory(TestTimeProvider clock) =>
        fixture.CreateFactory(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });

    private async Task SeedHouseholdAsync(string timezone)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Past date battery",
            Timezone = timezone,
            DefaultDinerCount = 4,
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
            MustChangePassword = false,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
        return user;
    }

    private async Task GrantAsync(long userId, string key)
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

    private async Task SeedEntryAsync(DateOnly date, CalendarEntryType type)
    {
        await using var context = fixture.CreateContext();
        context.CalendarEntries.Add(new CalendarEntry
        {
            MealDate = date,
            EntryType = type,
            SnapshotDisplayText = type.ToString(),
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
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
