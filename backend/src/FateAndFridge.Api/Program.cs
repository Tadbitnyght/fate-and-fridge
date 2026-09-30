using System.Text.Json;
using FateAndFridge.Api;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Default is not configured. " +
        "Set the ConnectionStrings__Default environment variable (see .env.example).");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());

// The internal API contract uses snake_case JSON property names (docs/API.md §3).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddScoped<SetupStateService>();

// Argon2id hashing is stateless and thread-safe (libsodium initializes once).
builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

builder.Services.AddOpenApi();

var app = builder.Build();

AppPaths.EnsureDataDirectories(app.Configuration, app.Logger);

var migrateOnly = args.Contains("--migrate");
if (migrateOnly || app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await DatabaseMigrator.MigrateWithRetryAsync(app.Services, app.Logger);
}

if (migrateOnly)
{
    return;
}

app.UseMiddleware<SetupGatingMiddleware>();

app.MapOpenApi();
app.MapHealthEndpoints();
app.MapSetupEndpoints();
app.MapFrontend();

await app.RunAsync();

/// <summary>Entry point marker used by the integration test host.</summary>
public partial class Program;
