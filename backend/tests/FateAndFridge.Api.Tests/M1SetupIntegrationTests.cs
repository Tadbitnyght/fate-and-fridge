using System.Net;
using System.Text.Json;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of setup state, <c>GET /setup/status</c>, and
/// pre-setup gating (docs/API.md §4 and §21). Requires a Docker daemon; run
/// with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class M1SetupIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Setup_status_reports_unconfigured_state_with_snake_case_fields()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/setup/status", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        payload.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("setup_completed", "auth_mode", "recovery_key_present");
        payload.RootElement.GetProperty("setup_completed").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("auth_mode").ValueKind.Should().Be(JsonValueKind.Null);
        payload.RootElement.GetProperty("recovery_key_present").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthMode.SingleUser, "single_user")]
    [InlineData(AuthMode.Multiuser, "multiuser")]
    public async Task Setup_status_reports_auth_mode_and_completed_flag(AuthMode authMode, string expectedAuthMode)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(authMode, cancellationToken);

        using var factory = fixture.CreateFactory();
        var client = factory.CreateClient();

        var before = await GetStatusAsync(client, cancellationToken);
        before.GetProperty("setup_completed").GetBoolean().Should().BeFalse();
        before.GetProperty("auth_mode").GetString().Should().Be(expectedAuthMode);

        using (var scope = factory.Services.CreateScope())
        {
            var setupState = scope.ServiceProvider.GetRequiredService<SetupStateService>();
            await setupState.SetSetupCompletedAsync(cancellationToken: cancellationToken);
        }

        var after = await GetStatusAsync(client, cancellationToken);
        after.GetProperty("setup_completed").GetBoolean().Should().BeTrue();
        after.GetProperty("auth_mode").GetString().Should().Be(expectedAuthMode);
    }

    [Fact]
    public async Task Recovery_key_presence_is_evaluated_per_request()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();

        try
        {
            using var factory = fixture.CreateFactory(dataDir);
            var client = factory.CreateClient();

            var before = await GetStatusAsync(client, cancellationToken);
            before.GetProperty("recovery_key_present").GetBoolean().Should().BeFalse();

            var configDir = Path.Combine(dataDir, "config");
            Directory.CreateDirectory(configDir);
            await File.WriteAllTextAsync(
                Path.Combine(configDir, "recovery.key"),
                "test-recovery-key",
                cancellationToken);

            var after = await GetStatusAsync(client, cancellationToken);
            after.GetProperty("recovery_key_present").GetBoolean().Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task Gated_api_route_returns_409_with_the_error_envelope_before_setup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/recipes", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));

        var error = payload.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.SetupRequired);
        error.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        error.GetProperty("details").ValueKind.Should().Be(JsonValueKind.Object);
        error.GetProperty("details").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task Health_setup_openapi_and_frontend_routes_are_reachable_before_setup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/health", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/health/db", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/setup/status", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/openapi/v1.json", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Gating_stops_without_a_restart_once_setup_completes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();
        var client = factory.CreateClient();

        var before = await client.GetAsync("/api/v1/no-such-route", cancellationToken);
        before.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using (var scope = factory.Services.CreateScope())
        {
            var setupState = scope.ServiceProvider.GetRequiredService<SetupStateService>();
            await setupState.SetSetupCompletedAsync(cancellationToken: cancellationToken);
        }

        var after = await client.GetAsync("/api/v1/no-such-route", cancellationToken);
        after.StatusCode.Should().NotBe(HttpStatusCode.Conflict);
        after.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Set_setup_completed_is_idempotent_and_updates_updated_at()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory();

        using var scope = factory.Services.CreateScope();
        var setupState = scope.ServiceProvider.GetRequiredService<SetupStateService>();

        await setupState.SetSetupCompletedAsync(cancellationToken: cancellationToken);

        // Age the row so the next write must visibly refresh updated_at.
        await using (var context = fixture.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE application_settings SET updated_at = now() - interval '1 day'",
                cancellationToken);
        }

        await setupState.SetSetupCompletedAsync(cancellationToken: cancellationToken);

        await using var verification = fixture.CreateContext();
        var rows = await verification.ApplicationSettings
            .Where(setting => setting.Key == SetupStateService.SetupCompletedKey)
            .ToListAsync(cancellationToken);

        rows.Should().ContainSingle();
        rows[0].ValueJson.Should().Be("true");
        rows[0].UpdatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    private static async Task<JsonElement> GetStatusAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync("/api/v1/setup/status", cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.Clone();
    }

    private async Task SeedHouseholdAsync(AuthMode authMode, CancellationToken cancellationToken)
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Integration Household",
            Timezone = "UTC",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string CreateTempDataDir() =>
        Path.Combine(Path.GetTempPath(), "fate-and-fridge-tests", Guid.NewGuid().ToString("N"));
}
