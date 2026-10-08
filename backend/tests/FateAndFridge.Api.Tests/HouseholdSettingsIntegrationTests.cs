using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of the household settings API
/// (docs/API.md §21): read, partial update, validation envelope, pre-setup
/// gating, and the M1-03 authorization seam. Requires a Docker daemon; run
/// with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class HouseholdSettingsIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Get_returns_the_snake_case_settings_payload_for_single_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await client.GetAsync("/api/v1/settings/household", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        payload.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal(
            "household_name",
            "timezone",
            "week_starts_on",
            "default_diner_count",
            "startup_page",
            "randomizer_repeat_lookback_days",
            "randomizer_repeat_lookahead_days",
            "randomizer_repeat_multiplier",
            "randomizer_special_day_multiplier",
            "randomizer_protein_multiplier",
            "randomizer_protein_neighbor_distance",
            "randomizer_favorite_multiplier",
            "randomizer_spacing_near_days",
            "randomizer_spacing_far_days",
            "randomizer_spacing_search_days");
        payload.RootElement.GetProperty("household_name").GetString().Should().Be("Test Household");
        payload.RootElement.GetProperty("timezone").GetString().Should().Be("America/Chicago");
        payload.RootElement.GetProperty("week_starts_on").GetInt32().Should().Be(0);
        payload.RootElement.GetProperty("default_diner_count").GetInt32().Should().Be(4);
        payload.RootElement.GetProperty("startup_page").GetString().Should().Be("calendar");

        // The seeded row omits the randomizer columns, so the database defaults
        // (docs/M6_PLAN.md §1.3) must come back on the GET payload.
        payload.RootElement.GetProperty("randomizer_repeat_lookback_days").GetInt32().Should().Be(7);
        payload.RootElement.GetProperty("randomizer_repeat_lookahead_days").GetInt32().Should().Be(14);
        payload.RootElement.GetProperty("randomizer_repeat_multiplier").GetDecimal().Should().Be(0.10m);
        payload.RootElement.GetProperty("randomizer_special_day_multiplier").GetDecimal().Should().Be(0.02m);
        payload.RootElement.GetProperty("randomizer_protein_multiplier").GetDecimal().Should().Be(0.70m);
        payload.RootElement.GetProperty("randomizer_protein_neighbor_distance").GetInt32().Should().Be(1);
        payload.RootElement.GetProperty("randomizer_favorite_multiplier").GetDecimal().Should().Be(1.10m);
        payload.RootElement.GetProperty("randomizer_spacing_near_days").GetInt32().Should().Be(3);
        payload.RootElement.GetProperty("randomizer_spacing_far_days").GetInt32().Should().Be(8);
        payload.RootElement.GetProperty("randomizer_spacing_search_days").GetInt32().Should().Be(90);
    }

    [Fact]
    public async Task Patch_updates_only_provided_fields_and_always_refreshes_updated_at()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        await AgeUpdatedAtAsync(cancellationToken);

        var response = await PatchAsync(
            client,
            """{"household_name": "  The Smiths  ", "default_diner_count": 6}""",
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)))
        {
            payload.RootElement.GetProperty("household_name").GetString().Should().Be("The Smiths");
            payload.RootElement.GetProperty("default_diner_count").GetInt32().Should().Be(6);
            payload.RootElement.GetProperty("timezone").GetString().Should().Be("America/Chicago");
            payload.RootElement.GetProperty("week_starts_on").GetInt32().Should().Be(0);
            payload.RootElement.GetProperty("startup_page").GetString().Should().Be("calendar");
        }

        await AssertPersistedAsync(
            settings =>
            {
                settings.HouseholdName.Should().Be("The Smiths");
                settings.DefaultDinerCount.Should().Be(6);
                settings.Timezone.Should().Be("America/Chicago");
                settings.WeekStartsOn.Should().Be(0);
                settings.StartupPage.Should().Be(StartupPage.Calendar);
                settings.CreatedAt.Should().BeCloseTo(SeedTimestamp, TimeSpan.FromSeconds(1));
                settings.UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            },
            cancellationToken);

        // Repeating current values is still a write and must refresh updated_at.
        await AgeUpdatedAtAsync(cancellationToken);

        var repeated = await PatchAsync(client, """{"week_starts_on": 0}""", cancellationToken);
        repeated.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertPersistedAsync(
            settings => settings.UpdatedAt.Should()
                .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)),
            cancellationToken);
    }

    [Theory]
    [InlineData("""{"household_name": ""}""", "household_name")]
    [InlineData("""{"timezone": "Mars/Olympus"}""", "timezone")]
    [InlineData("""{"week_starts_on": 7}""", "week_starts_on")]
    [InlineData("""{"default_diner_count": 0}""", "default_diner_count")]
    [InlineData("""{"startup_page": "Calendar"}""", "startup_page")]
    public async Task Patch_validation_failures_return_400_with_field_errors_and_do_not_write(
        string json,
        string expectedField)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(client, json, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var error = payload.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        var fieldErrors = error.GetProperty("details").GetProperty("field_errors");
        fieldErrors.TryGetProperty(expectedField, out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().BeGreaterThan(0);

        await AssertPersistedAsync(
            settings =>
            {
                settings.HouseholdName.Should().Be("Test Household");
                settings.Timezone.Should().Be("America/Chicago");
                settings.DefaultDinerCount.Should().Be(4);
                settings.WeekStartsOn.Should().Be(0);
                settings.StartupPage.Should().Be(StartupPage.Calendar);
            },
            cancellationToken);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"auth_mode": "multiuser"}""")]
    [InlineData("{not json")]
    public async Task Patch_without_a_known_field_or_with_invalid_json_returns_400(string json)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(client, json, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var error = payload.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("body", out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Patch_ignores_unknown_json_fields_and_keeps_auth_mode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(
            client,
            """{"household_name": "Renamed", "auth_mode": "multiuser", "unknown": {"nested": true}}""",
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        payload.RootElement.GetProperty("household_name").GetString().Should().Be("Renamed");

        await AssertPersistedAsync(
            settings =>
            {
                settings.HouseholdName.Should().Be("Renamed");
                settings.AuthMode.Should().Be(AuthMode.SingleUser);
            },
            cancellationToken);
    }

    [Fact]
    public async Task Patch_rejects_embedded_nul_in_household_name_without_writing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(
            client,
            """{"household_name": "bad\u0000name"}""",
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ((int)response.StatusCode).Should().BeLessThan(500);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var error = payload.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);

        var fieldErrors = error.GetProperty("details").GetProperty("field_errors");
        fieldErrors.TryGetProperty("household_name", out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().BeGreaterThan(0);

        await AssertPersistedAsync(
            settings => settings.HouseholdName.Should().Be("Test Household"),
            cancellationToken);
    }

    [Theory]
    [InlineData("america/chicago", "America/Chicago")]
    [InlineData("AMERICA/CHICAGO", "America/Chicago")]
    [InlineData("etc/gmt+5", "Etc/GMT+5")]
    public async Task Patch_stores_canonical_timezone_casing(string submitted, string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(
            client,
            JsonSerializer.Serialize(new { timezone = submitted }),
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)))
        {
            payload.RootElement.GetProperty("timezone").GetString().Should().Be(expected);
        }

        var getResponse = await client.GetAsync("/api/v1/settings/household", cancellationToken);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var payload = JsonDocument.Parse(
            await getResponse.Content.ReadAsStringAsync(cancellationToken)))
        {
            payload.RootElement.GetProperty("timezone").GetString().Should().Be(expected);
        }

        await AssertPersistedAsync(
            settings => settings.Timezone.Should().Be(expected),
            cancellationToken);
    }

    [Fact]
    public async Task Multiuser_without_a_session_returns_401_for_get_and_patch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.Multiuser, cancellationToken);

        var getResponse = await client.GetAsync("/api/v1/settings/household", cancellationToken);
        await AssertAuthRequiredAsync(getResponse, cancellationToken);

        var patchResponse = await PatchAsync(client, """{"household_name": "Denied"}""", cancellationToken);
        await AssertAuthRequiredAsync(patchResponse, cancellationToken);

        // Authorization is decided before validation: an empty body is still 401.
        var emptyPatch = await PatchAsync(client, "{}", cancellationToken);
        await AssertAuthRequiredAsync(emptyPatch, cancellationToken);
    }

    [Theory]
    [InlineData("authorization", false)]
    [InlineData("cookie", false)]
    [InlineData("api-key", false)]
    [InlineData("query", false)]
    [InlineData("authorization", true)]
    [InlineData("cookie", true)]
    [InlineData("api-key", true)]
    [InlineData("query", true)]
    public async Task Multiuser_denies_requests_carrying_fake_credentials(string credential, bool patch)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.Multiuser, cancellationToken);

        var response = await SendWithFakeCredentialAsync(client, credential, patch, cancellationToken);

        await AssertAuthRequiredAsync(response, cancellationToken);
    }

    [Fact]
    public async Task Settings_routes_return_409_setup_required_before_setup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = factory.CreateClient();

        var getResponse = await client.GetAsync("/api/v1/settings/household", cancellationToken);
        await AssertSetupRequiredAsync(getResponse, cancellationToken);

        var patchResponse = await PatchAsync(client, """{"household_name": "Early"}""", cancellationToken);
        await AssertSetupRequiredAsync(patchResponse, cancellationToken);
    }

    [Fact]
    public async Task Patch_is_forbidden_for_a_non_admin_actor_and_allowed_for_an_admin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(AuthMode.Multiuser, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        using var baseFactory = fixture.CreateFactory();
        using var memberFactory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<ICurrentActorProvider>(
                _ => new FixedActorProvider(new CurrentActor(IsSystem: false, Role: UserRole.Member)))));
        var memberClient = memberFactory.CreateClient();

        var memberRead = await memberClient.GetAsync("/api/v1/settings/household", cancellationToken);
        memberRead.StatusCode.Should().Be(HttpStatusCode.OK);

        var memberWrite = await PatchAsync(memberClient, """{"household_name": "Member"}""", cancellationToken);
        memberWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertErrorCodeAsync(memberWrite, ErrorCodes.Forbidden, cancellationToken);

        using var adminFactory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddScoped<ICurrentActorProvider>(
                _ => new FixedActorProvider(new CurrentActor(IsSystem: false, Role: UserRole.Admin)))));
        var adminClient = adminFactory.CreateClient();

        var adminWrite = await PatchAsync(adminClient, """{"household_name": "Admin"}""", cancellationToken);
        adminWrite.StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertPersistedAsync(
            settings => settings.HouseholdName.Should().Be("Admin"),
            cancellationToken);
    }

    [Fact]
    public async Task Patch_round_trips_every_randomizer_field_at_its_minimum()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(
            client,
            """
            {
              "randomizer_repeat_lookback_days": 0,
              "randomizer_repeat_lookahead_days": 0,
              "randomizer_repeat_multiplier": 0.01,
              "randomizer_special_day_multiplier": 0.00,
              "randomizer_protein_multiplier": 0.10,
              "randomizer_protein_neighbor_distance": 1,
              "randomizer_favorite_multiplier": 1.00,
              "randomizer_spacing_near_days": 1,
              "randomizer_spacing_far_days": 1,
              "randomizer_spacing_search_days": 7
            }
            """,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)))
        {
            payload.RootElement.GetProperty("randomizer_repeat_lookback_days").GetInt32().Should().Be(0);
            payload.RootElement.GetProperty("randomizer_repeat_lookahead_days").GetInt32().Should().Be(0);
            payload.RootElement.GetProperty("randomizer_repeat_multiplier").GetDecimal().Should().Be(0.01m);
            payload.RootElement.GetProperty("randomizer_special_day_multiplier").GetDecimal().Should().Be(0.00m);
            payload.RootElement.GetProperty("randomizer_protein_multiplier").GetDecimal().Should().Be(0.10m);
            payload.RootElement.GetProperty("randomizer_protein_neighbor_distance").GetInt32().Should().Be(1);
            payload.RootElement.GetProperty("randomizer_favorite_multiplier").GetDecimal().Should().Be(1.00m);
            payload.RootElement.GetProperty("randomizer_spacing_near_days").GetInt32().Should().Be(1);
            payload.RootElement.GetProperty("randomizer_spacing_far_days").GetInt32().Should().Be(1);
            payload.RootElement.GetProperty("randomizer_spacing_search_days").GetInt32().Should().Be(7);
        }

        await AssertPersistedAsync(
            settings =>
            {
                settings.RandomizerRepeatLookbackDays.Should().Be(0);
                settings.RandomizerRepeatLookaheadDays.Should().Be(0);
                settings.RandomizerRepeatMultiplier.Should().Be(0.01m);
                settings.RandomizerSpecialDayMultiplier.Should().Be(0.00m);
                settings.RandomizerProteinMultiplier.Should().Be(0.10m);
                settings.RandomizerProteinNeighborDistance.Should().Be(1);
                settings.RandomizerFavoriteMultiplier.Should().Be(1.00m);
                settings.RandomizerSpacingNearDays.Should().Be(1);
                settings.RandomizerSpacingFarDays.Should().Be(1);
                settings.RandomizerSpacingSearchDays.Should().Be(7);
            },
            cancellationToken);
    }

    [Fact]
    public async Task Patch_round_trips_every_randomizer_field_at_its_maximum()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(
            client,
            """
            {
              "randomizer_repeat_lookback_days": 60,
              "randomizer_repeat_lookahead_days": 60,
              "randomizer_repeat_multiplier": 1.00,
              "randomizer_special_day_multiplier": 1.00,
              "randomizer_protein_multiplier": 1.00,
              "randomizer_protein_neighbor_distance": 7,
              "randomizer_favorite_multiplier": 2.00,
              "randomizer_spacing_near_days": 30,
              "randomizer_spacing_far_days": 60,
              "randomizer_spacing_search_days": 365
            }
            """,
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken)))
        {
            payload.RootElement.GetProperty("randomizer_repeat_lookback_days").GetInt32().Should().Be(60);
            payload.RootElement.GetProperty("randomizer_repeat_lookahead_days").GetInt32().Should().Be(60);
            payload.RootElement.GetProperty("randomizer_repeat_multiplier").GetDecimal().Should().Be(1.00m);
            payload.RootElement.GetProperty("randomizer_special_day_multiplier").GetDecimal().Should().Be(1.00m);
            payload.RootElement.GetProperty("randomizer_protein_multiplier").GetDecimal().Should().Be(1.00m);
            payload.RootElement.GetProperty("randomizer_protein_neighbor_distance").GetInt32().Should().Be(7);
            payload.RootElement.GetProperty("randomizer_favorite_multiplier").GetDecimal().Should().Be(2.00m);
            payload.RootElement.GetProperty("randomizer_spacing_near_days").GetInt32().Should().Be(30);
            payload.RootElement.GetProperty("randomizer_spacing_far_days").GetInt32().Should().Be(60);
            payload.RootElement.GetProperty("randomizer_spacing_search_days").GetInt32().Should().Be(365);
        }

        await AssertPersistedAsync(
            settings =>
            {
                settings.RandomizerRepeatLookbackDays.Should().Be(60);
                settings.RandomizerRepeatLookaheadDays.Should().Be(60);
                settings.RandomizerRepeatMultiplier.Should().Be(1.00m);
                settings.RandomizerSpecialDayMultiplier.Should().Be(1.00m);
                settings.RandomizerProteinMultiplier.Should().Be(1.00m);
                settings.RandomizerProteinNeighborDistance.Should().Be(7);
                settings.RandomizerFavoriteMultiplier.Should().Be(2.00m);
                settings.RandomizerSpacingNearDays.Should().Be(30);
                settings.RandomizerSpacingFarDays.Should().Be(60);
                settings.RandomizerSpacingSearchDays.Should().Be(365);
            },
            cancellationToken);
    }

    [Theory]
    [InlineData("""{"randomizer_repeat_lookback_days": -1}""", "randomizer_repeat_lookback_days")]
    [InlineData("""{"randomizer_repeat_lookback_days": 61}""", "randomizer_repeat_lookback_days")]
    [InlineData("""{"randomizer_repeat_lookahead_days": -1}""", "randomizer_repeat_lookahead_days")]
    [InlineData("""{"randomizer_repeat_lookahead_days": 61}""", "randomizer_repeat_lookahead_days")]
    [InlineData("""{"randomizer_repeat_multiplier": 0.00}""", "randomizer_repeat_multiplier")]
    [InlineData("""{"randomizer_repeat_multiplier": 1.01}""", "randomizer_repeat_multiplier")]
    [InlineData("""{"randomizer_special_day_multiplier": -0.01}""", "randomizer_special_day_multiplier")]
    [InlineData("""{"randomizer_special_day_multiplier": 1.01}""", "randomizer_special_day_multiplier")]
    [InlineData("""{"randomizer_protein_multiplier": 0.09}""", "randomizer_protein_multiplier")]
    [InlineData("""{"randomizer_protein_multiplier": 1.01}""", "randomizer_protein_multiplier")]
    [InlineData("""{"randomizer_protein_neighbor_distance": 0}""", "randomizer_protein_neighbor_distance")]
    [InlineData("""{"randomizer_protein_neighbor_distance": 8}""", "randomizer_protein_neighbor_distance")]
    [InlineData("""{"randomizer_favorite_multiplier": 0.99}""", "randomizer_favorite_multiplier")]
    [InlineData("""{"randomizer_favorite_multiplier": 2.01}""", "randomizer_favorite_multiplier")]
    [InlineData("""{"randomizer_spacing_near_days": 0}""", "randomizer_spacing_near_days")]
    [InlineData("""{"randomizer_spacing_near_days": 31}""", "randomizer_spacing_near_days")]
    [InlineData("""{"randomizer_spacing_far_days": 0}""", "randomizer_spacing_far_days")]
    [InlineData("""{"randomizer_spacing_far_days": 61}""", "randomizer_spacing_far_days")]
    [InlineData("""{"randomizer_spacing_search_days": 6}""", "randomizer_spacing_search_days")]
    [InlineData("""{"randomizer_spacing_search_days": 366}""", "randomizer_spacing_search_days")]
    public async Task Patch_rejects_randomizer_fields_just_outside_their_range(
        string json,
        string expectedField)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var response = await PatchAsync(client, json, cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var error = payload.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);

        var fieldErrors = error.GetProperty("details").GetProperty("field_errors");
        fieldErrors.TryGetProperty(expectedField, out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().BeGreaterThan(0);

        await AssertPersistedAsync(
            settings =>
            {
                settings.RandomizerRepeatLookbackDays.Should().Be(7);
                settings.RandomizerRepeatLookaheadDays.Should().Be(14);
                settings.RandomizerRepeatMultiplier.Should().Be(0.10m);
                settings.RandomizerSpecialDayMultiplier.Should().Be(0.02m);
                settings.RandomizerProteinMultiplier.Should().Be(0.70m);
                settings.RandomizerProteinNeighborDistance.Should().Be(1);
                settings.RandomizerFavoriteMultiplier.Should().Be(1.10m);
                settings.RandomizerSpacingNearDays.Should().Be(3);
                settings.RandomizerSpacingFarDays.Should().Be(8);
                settings.RandomizerSpacingSearchDays.Should().Be(90);
            },
            cancellationToken);
    }

    [Fact]
    public async Task Patch_rejects_spacing_far_below_near_and_accepts_equal_values()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        var rejected = await PatchAsync(
            client,
            """{"randomizer_spacing_near_days": 10, "randomizer_spacing_far_days": 5}""",
            cancellationToken);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using (var payload = JsonDocument.Parse(
            await rejected.Content.ReadAsStringAsync(cancellationToken)))
        {
            var fieldErrors = payload.RootElement
                .GetProperty("error")
                .GetProperty("details")
                .GetProperty("field_errors");
            fieldErrors.TryGetProperty("randomizer_spacing_far_days", out var messages).Should().BeTrue();
            messages.GetArrayLength().Should().BeGreaterThan(0);
        }

        await AssertPersistedAsync(
            settings =>
            {
                settings.RandomizerSpacingNearDays.Should().Be(3);
                settings.RandomizerSpacingFarDays.Should().Be(8);
            },
            cancellationToken);

        var accepted = await PatchAsync(
            client,
            """{"randomizer_spacing_near_days": 10, "randomizer_spacing_far_days": 10}""",
            cancellationToken);

        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Patch_rejects_spacing_near_raised_above_the_stored_far()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = await CreateConfiguredClientAsync(factory, AuthMode.SingleUser, cancellationToken);

        // The stored far is 8, so raising near to 12 alone must fail even though
        // the patch never mentions the far field.
        var response = await PatchAsync(
            client,
            """{"randomizer_spacing_near_days": 12}""",
            cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var fieldErrors = payload.RootElement
            .GetProperty("error")
            .GetProperty("details")
            .GetProperty("field_errors");
        fieldErrors.TryGetProperty("randomizer_spacing_far_days", out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().BeGreaterThan(0);

        await AssertPersistedAsync(
            settings => settings.RandomizerSpacingNearDays.Should().Be(3),
            cancellationToken);
    }

    private async Task<HttpClient> CreateConfiguredClientAsync(
        ApiFactory factory,
        AuthMode authMode,
        CancellationToken cancellationToken)
    {
        await SeedHouseholdAsync(authMode, cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);
        return factory.CreateClient();
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Test Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkSetupCompletedAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }

    private async Task AgeUpdatedAtAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE household_settings SET updated_at = now() - interval '1 day'",
            cancellationToken);
    }

    private async Task AssertPersistedAsync(
        Action<HouseholdSettings> assertion,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var settings = await context.HouseholdSettings.AsNoTracking().SingleAsync(cancellationToken);
        assertion(settings);
    }

    private static async Task<HttpResponseMessage> SendWithFakeCredentialAsync(
        HttpClient client,
        string credential,
        bool patch,
        CancellationToken cancellationToken)
    {
        var path = "/api/v1/settings/household";
        if (credential == "query")
        {
            path += "?user=admin&user_id=1";
        }

        using var request = new HttpRequestMessage(patch ? HttpMethod.Patch : HttpMethod.Get, path);
        if (patch)
        {
            request.Content = new StringContent(
                """{"household_name": "Fake"}""",
                Encoding.UTF8,
                "application/json");
        }

        switch (credential)
        {
            case "authorization":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-session");
                break;
            case "cookie":
                request.Headers.Add("Cookie", "fatefridge_session=not-a-session");
                break;
            case "api-key":
                request.Headers.Add("X-Api-Key", "not-a-session");
                break;
            case "query":
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(credential),
                    credential,
                    "Unknown credential style.");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        string json,
        CancellationToken cancellationToken) =>
        await client.PatchAsync(
            "/api/v1/settings/household",
            new StringContent(json, Encoding.UTF8, "application/json"),
            cancellationToken);

    private static async Task AssertAuthRequiredAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await AssertErrorCodeAsync(response, ErrorCodes.AuthRequired, cancellationToken);
    }

    private static async Task AssertSetupRequiredAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertErrorCodeAsync(response, ErrorCodes.SetupRequired, cancellationToken);
    }

    private static async Task AssertErrorCodeAsync(
        HttpResponseMessage response,
        string expectedCode,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        var error = payload.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(expectedCode);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        error.GetProperty("details").ValueKind.Should().Be(JsonValueKind.Object);
    }

    private sealed class FixedActorProvider(CurrentActor? actor) : ICurrentActorProvider
    {
        public Task<CurrentActor?> GetCurrentActorAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(actor);
    }
}
