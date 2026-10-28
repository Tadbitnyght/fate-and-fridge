using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Shared PostgreSQL container for the statistics tests. Migrations are
/// applied once; <see cref="ResetAsync"/> clears the volatile rows so every test
/// starts from a clean slate. Reference data (proteins) is left in place (or
/// created on demand) because it survives across tests.
/// </summary>
public sealed class StatisticsFixture : IAsyncLifetime
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
            "The M11 statistics fixture has not been initialized."));

    public ApiFactory CreateFactory(Action<IServiceCollection>? configureServices = null) =>
        new(ConnectionString, migrateOnStartup: false, dataDir: null, configureServices);

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM grocery_check_state;
            DELETE FROM manual_grocery_items;
            DELETE FROM grocery_staples;
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
            DELETE FROM user_grocery_preferences;
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
}

[CollectionDefinition(StatisticsCollection.Name)]
public sealed class StatisticsCollection : ICollectionFixture<StatisticsFixture>
{
    public const string Name = "M11 statistics";
}
