using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Shared PostgreSQL container for the M8-01 grocery schema tests. Migrations
/// are applied once; each test creates its own short-lived
/// <see cref="AppDbContext"/>.
/// </summary>
public sealed class M8GroceriesSchemaFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    private DbContextOptions<AppDbContext>? _options;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await _postgres.StartAsync(cancellationToken);

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => new(_postgres.DisposeAsync().AsTask());

    public AppDbContext CreateContext() =>
        new(_options ?? throw new InvalidOperationException(
            "The M8 grocery schema fixture has not been initialized."));
}

[CollectionDefinition(M8GroceriesSchemaCollection.Name)]
public sealed class M8GroceriesSchemaCollection : ICollectionFixture<M8GroceriesSchemaFixture>
{
    public const string Name = "M8 grocery schema";
}
