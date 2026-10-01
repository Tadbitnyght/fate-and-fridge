using System.Text.Json;
using FateAndFridge.Api;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

// The Data Protection key ring must live on the persistent volume so opaque
// sessions and antiforgery tokens survive container replacement
// (docs/MILESTONES.md M1 decisions, docs/SECURITY.md §2).
builder.Services
    .AddDataProtection()
    .SetApplicationName("FateAndFridge")
    .PersistKeysToFileSystem(new DirectoryInfo(AppPaths.DataProtectionKeysRoot(builder.Configuration)));

// Antiforgery backs the X-CSRF-TOKEN header contract (docs/API.md §5).
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = CsrfMiddleware.HeaderName;
    options.Cookie.Name = "faf_csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Session-scoped antiforgery tokens: rotation and cross-session reuse fail.
builder.Services.AddSingleton<IAntiforgeryAdditionalDataProvider, SessionAntiforgeryDataProvider>();

// The internal API contract uses snake_case JSON property names (docs/API.md §3).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});

builder.Services.AddScoped<SetupStateService>();

// M1-04: single-shot setup completion and the recovery key file store.
builder.Services.AddScoped<SetupCompletionService>();
builder.Services.AddScoped<RecoveryKeyStore>();

// M1-10: user management, last-active-Admin safety, and the auth-mode switch.
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<AuthModeService>();

// M1-11: security settings (IP allowlist + trusted proxies) with a short-TTL
// cache so the per-request middleware does not query the database every time.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<SecuritySettingsService>();

builder.Services.AddScoped<HouseholdSettingsService>();

// M1-06 session infrastructure: opaque DB-backed cookie sessions resolved by
// the default authentication scheme, with the session-aware actor provider
// replacing the M1-03 interim provider. M1-07 adds login/logout endpoints and
// M1-09 re-tests the multiuser permission matrix.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IAuthEventWriter, AuthEventWriter>();

// M2-02: reference-data audit writer. Rows are staged on the caller's
// AppDbContext so they commit atomically with the change they describe.
builder.Services.AddScoped<IAuditService, AuditService>();

// M2-04: idempotent startup seeder for the default reference data. It runs
// once after migrations, never overwrites user edits, and serializes
// concurrent startups with a transaction-scoped PostgreSQL advisory lock.
builder.Services.AddScoped<ReferenceDataSeeder>();

// M1-09 permission model: one uncached grant reader plus the evaluator service
// shared by GET /auth/session and the route enforcement filter.
builder.Services.AddScoped<IPermissionGrantReader, DbPermissionGrantReader>();
builder.Services.AddScoped<PermissionService>();

// M1-07 part 2: per-account lockout and progressive delay
// (docs/SECURITY.md §3) plus a process-local, bounded per-IP failure throttle.
builder.Services
    .AddOptions<LoginThrottleOptions>()
    .Bind(builder.Configuration.GetSection(LoginThrottleOptions.SectionName))
    .Validate(
        options => options.MaxFailuresPerWindow >= 1
            && options.WindowSeconds >= 1
            && options.MaxTrackedAddresses >= 1,
        "Auth:LoginThrottle limits must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<ILoginIpThrottle, LoginIpThrottle>();
builder.Services.AddSingleton<ILoginAttemptPolicy, LockoutLoginAttemptPolicy>();
builder.Services.AddSingleton<ILoginAttemptDelayer, TimeProviderLoginAttemptDelayer>();

builder.Services.AddAuthentication(SessionAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
        SessionAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorization();
builder.Services.AddScoped<ICurrentActorProvider, SessionActorProvider>();

// Argon2id hashing is stateless and thread-safe (libsodium initializes once).
builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

builder.Services.AddOpenApi();

var app = builder.Build();

AppPaths.EnsureDataDirectories(app.Configuration, app.Logger);

var migrateOnly = args.Contains("--migrate");
if (migrateOnly || app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await DatabaseMigrator.MigrateWithRetryAsync(app.Services, app.Logger);
    await ReferenceDataSeeder.SeedAtStartupAsync(app.Services, app.Logger);
}

if (migrateOnly)
{
    return;
}

// Order matters: the exception handler wraps everything so unexpected failures
// still use the shared error envelope; the client address (trusted-proxy
// forwarded headers) resolves next so the allowlist, Secure-cookie decisions,
// and antiforgery all see the real client; the allowlist then rejects blocked
// clients before any authentication or setup state is consulted; setup gating
// still wins pre-setup (409) for allowed clients; the forced-password-change
// gate runs before CSRF so a flagged session always gets
// PASSWORD_CHANGE_REQUIRED on gated routes.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<ClientAddressMiddleware>();
app.UseMiddleware<IpAllowlistMiddleware>();
app.UseAuthentication();
app.UseMiddleware<SetupGatingMiddleware>();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();

app.MapOpenApi();
app.MapHealthEndpoints();
app.MapSetupEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapSettingsEndpoints();

// Test-only permission probes (M1-09): mapped solely in the Testing
// environment so the permission matrix can exercise server-side enforcement.
// They are regular /api/v1 routes, subject to setup gating and the
// forced-change gate, and expose no secrets.
if (app.Environment.IsEnvironment("Testing"))
{
    app.MapPermissionProbeEndpoints();
}

app.MapFrontend();

await app.RunAsync();

/// <summary>Entry point marker used by the integration test host.</summary>
public partial class Program;
