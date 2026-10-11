using System.Text.Json;
using FateAndFridge.Api;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
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
builder.Services.AddScoped<HouseholdClock>();

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

// M2-05: departments management (ordered reference data with audit).
builder.Services.AddScoped<DepartmentService>();

// M2-06: units and unit aliases, plus the recipe-usage seam consumed by the
// reference-data hard deletes (M2-08/M2-10 consume the other methods).
builder.Services.AddScoped<UnitService>();

// M2-08: ingredients and their aliases; the canonical name and every alias
// share one global uniqueness namespace.
builder.Services.AddScoped<IngredientService>();

// M2-10: proteins and tags management, plus the recipe-usage seams consumed
// by their Admin-only hard deletes.
builder.Services.AddScoped<ProteinService>();
builder.Services.AddScoped<TagService>();

// M3-06/M3-08: the recipe tables now exist, so the usage seam counts and
// reassigns real recipe references instead of the M2 zero-returning stub.
builder.Services.AddScoped<IReferenceUsageChecker, ReferenceDataUsageChecker>();

// M3-02…M3-09: recipe document CRUD, status lifecycle, and nested slices.
builder.Services.AddScoped<RecipeService>();
builder.Services.AddScoped<CalendarService>();

// M6-08: the single-date reroll orchestrator (context build + atomic history).
builder.Services.AddScoped<CalendarRandomizer>();

// M7-01: the batch reroll preview service (draft-backed sequential engine pass).
builder.Services.AddScoped<BatchRerollService>();

// M6-02: the pure randomization engine and its injectable secure RNG seam. The
// engine scores candidates and performs a weighted roulette draw; SystemRandomSource
// is the only production source of randomness (docs/RANDOMIZER.md §7 Phase F).
builder.Services.AddSingleton<IRandomSource, SystemRandomSource>();
builder.Services.AddSingleton<RandomizerEngine>();

// M3-04: local recipe image storage, content policy, and serving.
builder.Services.AddScoped<RecipeImageService>();

// M4-01: SSRF-hardened outbound fetcher shared by recipe import (M4-04) and
// the image-from-URL endpoint (M4-07). Stateless; a single HttpClient with a
// pinned connect callback is reused.
builder.Services
    .AddOptions<ImportFetchOptions>()
    .Bind(builder.Configuration.GetSection(ImportFetchOptions.SectionName))
    .Validate(
        options => options.MaxRedirects >= 0
            && options.ConnectTimeout > TimeSpan.Zero
            && options.ReadTimeout > TimeSpan.Zero
            && options.MaxHtmlBytes > 0
            && options.MaxImageBytes > 0
            && ImportFetchOptions.HasValidContentTypes(options.AllowedPageContentTypes)
            && ImportFetchOptions.HasValidContentTypes(options.AllowedImageContentTypes),
        "Imports:Fetch limits must be positive and both content-type allowlists must contain only valid media types.")
    .ValidateOnStart();
builder.Services.AddSingleton<IDnsResolver, SystemDnsResolver>();
builder.Services.AddSingleton<IImportFetcher>(serviceProvider => new SafeUrlFetcher(
    serviceProvider.GetRequiredService<ILogger<SafeUrlFetcher>>(),
    serviceProvider.GetRequiredService<IOptions<ImportFetchOptions>>(),
    serviceProvider.GetRequiredService<IDnsResolver>()));
// M4-04: per-user import fetch throttling (30 page / 60 image fetches per
// hour by default) and the draft-only import orchestration service.
builder.Services
    .AddOptions<ImportThrottleOptions>()
    .Bind(builder.Configuration.GetSection(ImportThrottleOptions.SectionName))
    .Validate(
        options => options.PageFetchesPerHour >= 1
            && options.ImageFetchesPerHour >= 1
            && options.WindowSeconds >= 1
            && options.MaxTrackedActors >= 1,
        "Imports:Throttle limits must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<IImportThrottle, ImportThrottle>();
builder.Services.AddScoped<RecipeImportService>();

// M4-07: image-from-URL download orchestration. It shares the SSRF-hardened
// fetcher and the M3 image storage/content policy instead of reimplementing
// either.
builder.Services.AddScoped<RecipeImageImportService>();

// M3-04: bound multipart parsing for image uploads before anything is
// buffered; the endpoint enforces the exact 5 MiB file cap and the format
// policy on top of this.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = RecipeImageService.MaxMultipartBodyBytes;
});

// M2-07: pure, dependency-free conversion between compatible units; stateless,
// so a singleton. M8 grocery aggregation consumes it via DI.
builder.Services.AddSingleton<UnitConversionService>();

// M8-02: pure grocery aggregation over supplied recipe ingredients; stateless and
// I/O-free, so a singleton over the conversion service above.
builder.Services.AddSingleton<GroceryAggregationService>();

// M8-03: the live grocery list projection over calendar, recipe, manual, and
// check-state rows. Scoped because it reads through the request AppDbContext.
builder.Services.AddScoped<GroceryListService>();

// M8-04: range-scoped manual grocery CRUD and the per-user (or system-actor)
// preference store, both scoped over the request AppDbContext.
builder.Services.AddScoped<GroceryManualItemService>();
builder.Services.AddScoped<GroceryPreferenceService>();

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
}

// M2-04: seeding runs on every startup, even when automatic migrations are
// disabled, so the reference defaults exist regardless of deployment config.
await ReferenceDataSeeder.SeedAtStartupAsync(app.Services, app.Logger);

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
app.MapDepartmentEndpoints();
app.MapUnitEndpoints();
app.MapIngredientEndpoints();
app.MapProteinEndpoints();
app.MapTagEndpoints();
app.MapRecipeEndpoints();
app.MapRecipeImageEndpoints();
app.MapCalendarEndpoints();
app.MapGroceryEndpoints();
app.MapOperationEndpoints();
app.MapImportEndpoints();

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
