using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// PostgreSQL fixture for the M2-04 reference-data seeder. The container starts
/// fully migrated; tests reset the reference data or roll the migration history
/// back to the M1 baseline as needed (docs/M2_PLAN.md §2).
/// </summary>
public sealed class ReferenceDataSeederFixture : IAsyncLifetime
{
    public const string M1BaselineMigrationId = "20260930015712_M1SchemaBaseline";

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
            "The reference-data seeder fixture has not been initialized."));

    /// <summary>Creates a context with an extra command interceptor (used to poison one seed statement).</summary>
    public AppDbContext CreateContextWithInterceptor(DbCommandInterceptor interceptor)
    {
        _ = _options ?? throw new InvalidOperationException(
            "The reference-data seeder fixture has not been initialized.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;

        return new AppDbContext(options);
    }

    /// <summary>
    /// Deletes every reference-data row and all application settings so the next
    /// seeder run starts from a database that has never been seeded.
    /// </summary>
    public async Task ResetReferenceDataAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM unit_aliases;
            DELETE FROM units;
            DELETE FROM ingredients;
            DELETE FROM proteins;
            DELETE FROM grocery_departments;
            DELETE FROM application_settings;
            """,
            cancellationToken);
    }

    /// <summary>Rolls the migration history back to the M1 baseline (no reference-data tables).</summary>
    public async Task MigrateToM1BaselineAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.GetService<IMigrator>().MigrateAsync(M1BaselineMigrationId, cancellationToken);
    }

    /// <summary>Applies every migration, as upgrading an older install would.</summary>
    public async Task MigrateToLatestAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
    }
}

[CollectionDefinition(ReferenceDataSeederCollection.Name)]
public sealed class ReferenceDataSeederCollection : ICollectionFixture<ReferenceDataSeederFixture>
{
    public const string Name = "Reference data seeder";
}
