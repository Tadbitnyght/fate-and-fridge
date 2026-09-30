using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Shared PostgreSQL container for the M1-02 setup-state and gating tests.
/// Migrations are applied once; each test resets the setup rows through
/// <see cref="ResetAsync"/> so no test depends on execution order.
/// </summary>
public sealed class M1SetupFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    private DbContextOptions<AppDbContext>? _options;

    public string ConnectionString => _postgres.GetConnectionString();

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
            "The M1 setup fixture has not been initialized."));

    public ApiFactory CreateFactory(string? dataDir = null) =>
        new(ConnectionString, migrateOnStartup: false, dataDir);

    /// <summary>Removes setup state so every test starts from a fresh install.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM application_settings; DELETE FROM household_settings;",
            cancellationToken);
    }
}

[CollectionDefinition(M1SetupCollection.Name)]
public sealed class M1SetupCollection : ICollectionFixture<M1SetupFixture>
{
    public const string Name = "M1 setup";
}
