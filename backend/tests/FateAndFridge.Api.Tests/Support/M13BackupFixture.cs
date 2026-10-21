using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// PostgreSQL container plus an isolated data root for the M13-03 backup
/// service/API tests. Migrations are applied once from scratch; every test
/// resets the database and uses its own data directory so archives, images, and
/// recovery keys never leak between tests.
/// </summary>
public sealed class M13BackupFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    private DbContextOptions<AppDbContext>? _options;
    private string? _dataRoot;

    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Root under which each test creates its own <c>DataDir</c>.</summary>
    public string DataRoot => _dataRoot ?? throw new InvalidOperationException(
        "The M13 backup fixture has not been initialized.");

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

        var parent = Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath();
        _dataRoot = Directory.CreateDirectory(
            Path.Combine(parent, $"faf-m13-backups-{Guid.NewGuid():N}")).FullName;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataRoot is not null)
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover test directory is harmless.
            }
        }

        await _postgres.DisposeAsync();
    }

    public AppDbContext CreateContext() =>
        new(_options ?? throw new InvalidOperationException(
            "The M13 backup fixture has not been initialized."));

    public ApiFactory CreateFactory(
        string dataDir,
        Action<IServiceCollection>? configureServices = null) =>
        new(ConnectionString, migrateOnStartup: false, dataDir, configureServices);

    /// <summary>A fresh, empty data directory for one test.</summary>
    public string NewDataDirectory() =>
        Directory.CreateDirectory(Path.Combine(DataRoot, Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>
    /// Removes every row these tests create, in foreign-key order. The backups
    /// table references users with RESTRICT, so it is cleared first.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            DELETE FROM backup_metadata;
            DELETE FROM grocery_check_state;
            DELETE FROM manual_grocery_items;
            DELETE FROM grocery_staples;
            DELETE FROM user_grocery_preferences;
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
}

[CollectionDefinition(M13BackupCollection.Name)]
public sealed class M13BackupCollection : ICollectionFixture<M13BackupFixture>
{
    public const string Name = "M13 backup service";
}
