using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed integration tests. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DatabaseIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public ValueTask InitializeAsync() =>
        new(_postgres.StartAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    [Fact]
    public async Task Startup_Applies_Migrations_And_Reports_Connected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = new ApiFactory(_postgres.GetConnectionString(), migrateOnStartup: true);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health/db", cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        payload.GetProperty("status").GetString().Should().Be("ok");
        payload.GetProperty("database").GetString().Should().Be("connected");

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var applied = await dbContext.Database.GetAppliedMigrationsAsync(cancellationToken);
        applied.Should().NotBeEmpty();
    }
}
