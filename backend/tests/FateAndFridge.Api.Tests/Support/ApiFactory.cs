using FateAndFridge.Api.LiveSync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// In-memory test host for the API. The connection string is a dummy by
/// default; only the Testcontainers integration test points it at a real
/// database (and enables startup migrations).
/// </summary>
public sealed class ApiFactory(
    string connectionString = ApiFactory.DummyConnectionString,
    bool migrateOnStartup = false,
    string? dataDir = null,
    Action<IServiceCollection>? configureServices = null,
    RecordingLiveSyncPublisher? liveSyncPublisher = null,
    bool startupUpdateCheck = false) : WebApplicationFactory<Program>
{
    public const string DummyConnectionString =
        "Host=localhost;Port=5432;Database=fatefridge_tests;Username=fatefridge;Password=fatefridge";

    /// <summary>
    /// When supplied, the recording <see cref="ILiveSyncPublisher"/> registered
    /// into the test host so a test can assert the exact events a committed
    /// write emitted (M9-02…M9-05) without opening a socket.
    /// </summary>
    public RecordingLiveSyncPublisher? LiveSyncPublisher { get; } = liveSyncPublisher;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Test hosts run in the Testing environment so they are distinct from
        // Development/Production configuration; the product route surface is
        // identical. Scope validation stays on so captive-dependency
        // regressions are caught in tests.
        builder.UseEnvironment("Testing");
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");

        // M13-09: the startup update check is off by default in tests so no
        // test host reaches the public network; the update-check suite opts in
        // with its own stub transport (docs/M13_PLAN.md §1.13).
        builder.UseSetting("Updates:StartupCheckEnabled", startupUpdateCheck ? "true" : "false");
        if (dataDir is not null)
        {
            builder.UseSetting("DataDir", dataDir);
        }

        // Tests inject counting IReferenceUsageChecker fakes (M2-06 seam), the
        // recording live-sync publisher, and other overrides without replacing
        // the whole factory.
        if (LiveSyncPublisher is not null || configureServices is not null)
        {
            builder.ConfigureTestServices(services =>
            {
                if (LiveSyncPublisher is not null)
                {
                    services.RemoveAll<ILiveSyncPublisher>();
                    services.AddSingleton<ILiveSyncPublisher>(LiveSyncPublisher);
                }

                configureServices?.Invoke(services);
            });
        }
    }
}
