using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// In-memory test host for the API. The connection string is a dummy by
/// default; only the Testcontainers integration test points it at a real
/// database (and enables startup migrations).
/// </summary>
public sealed class ApiFactory(
    string connectionString = ApiFactory.DummyConnectionString,
    bool migrateOnStartup = false,
    string? dataDir = null) : WebApplicationFactory<Program>
{
    public const string DummyConnectionString =
        "Host=localhost;Port=5432;Database=fatefridge_tests;Username=fatefridge;Password=fatefridge";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The Testing environment enables the test-only permission probe routes
        // (M1-09); it never runs in production. Scope validation stays on so
        // captive-dependency regressions are caught in tests.
        builder.UseEnvironment("Testing");
        builder.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");
        if (dataDir is not null)
        {
            builder.UseSetting("DataDir", dataDir);
        }
    }
}
