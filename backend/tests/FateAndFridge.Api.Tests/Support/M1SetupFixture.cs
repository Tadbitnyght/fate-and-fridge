using FateAndFridge.Api.Data;
using FateAndFridge.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    public ApiFactory CreateFactory(
        string? dataDir = null,
        Action<IServiceCollection>? configureServices = null) =>
        new(ConnectionString, migrateOnStartup: false, dataDir, configureServices);

    /// <summary>
    /// Removes setup state, M1 authentication rows, M3 recipe rows, M5 calendar
    /// rows, and staged audit rows so every test starts from a fresh install.
    /// Sessions, audit events, and calendar rows first: they reference users
    /// with RESTRICT. Recipe children are removed before recipes so the shared
    /// reset never violates a foreign key.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM calendar_entry_history;
            DELETE FROM calendar_entries;
            DELETE FROM recipe_tags;
            DELETE FROM recipe_proteins;
            DELETE FROM recipe_instruction_steps;
            DELETE FROM recipe_instruction_sections;
            DELETE FROM recipe_ingredients;
            DELETE FROM recipe_ingredient_groups;
            DELETE FROM recipe_images;
            DELETE FROM recipes;
            DELETE FROM user_sessions;
            DELETE FROM user_permissions;
            DELETE FROM auth_events;
            DELETE FROM audit_events;
            DELETE FROM users;
            DELETE FROM application_settings;
            DELETE FROM household_settings;
            """,
            cancellationToken);
    }

    /// <summary>
    /// Resets all M1 state and returns a factory whose setup is already
    /// completed (so gated routes are reachable in tests).
    /// </summary>
    public async Task<ApiFactory> CreateConfiguredFactoryAsync(
        CancellationToken cancellationToken,
        string? dataDir = null)
    {
        await ResetAsync(cancellationToken);
        var factory = CreateFactory(dataDir);

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SetupStateService>()
            .SetSetupCompletedAsync(true, cancellationToken);

        return factory;
    }
}

[CollectionDefinition(M1SetupCollection.Name)]
public sealed class M1SetupCollection : ICollectionFixture<M1SetupFixture>
{
    public const string Name = "M1 setup";
}
