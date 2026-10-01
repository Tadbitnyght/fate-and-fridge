using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Host-composition smoke tests for the health and root endpoints. The host
/// seeds reference data on every startup, so these tests need a reachable
/// PostgreSQL container.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HealthEndpointsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public ValueTask InitializeAsync() =>
        new(_postgres.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Health_Returns_Application_Identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        payload.GetProperty("status").GetString().Should().Be("ok");
        payload.GetProperty("app").GetString().Should().Be("Fate & Fridge");
        payload.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        payload.GetProperty("environment").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Root_Returns_Json_Index_When_Frontend_Is_Not_Built()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = CreateFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        payload.GetProperty("app").GetString().Should().Be("Fate & Fridge");
        payload.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
    }

    private ApiFactory CreateFactory() =>
        new(_postgres.GetConnectionString(), migrateOnStartup: true, CreateTempDataDir());

    private static string CreateTempDataDir() =>
        Path.Combine(Path.GetTempPath(), "fate-and-fridge-tests", Guid.NewGuid().ToString("N"));
}
