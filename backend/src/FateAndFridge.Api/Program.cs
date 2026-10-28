using System.Text.Json;
using FateAndFridge.Api;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Imports;
using FateAndFridge.Api.LiveSync;
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

// Single-shot setup completion and the recovery key file store.
builder.Services.AddScoped<SetupCompletionService>();
builder.Services.AddScoped<RecoveryKeyStore>();

// User management, last-active-Admin safety, and the auth-mode switch.
builder.Services.AddScoped<UserManagementService>();
builder.Services.AddScoped<AuthModeService>();

// Security settings (IP allowlist + trusted proxies) with a short-TTL
// cache so the per-request middleware does not query the database every time.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<SecuritySettingsService>();

builder.Services.AddScoped<HouseholdSettingsService>();
builder.Services.AddScoped<HouseholdClock>();

// Session infrastructure: opaque DB-backed cookie sessions resolved by
// the default authentication scheme, with the session-aware actor provider
// replacing the interim provider. adds login/logout endpoints and
// Re-tests the multiuser permission matrix.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<IAuthEventWriter, AuthEventWriter>();

// Reference-data audit writer. Rows are staged on the caller's
// AppDbContext so they commit atomically with the change they describe.
builder.Services.AddScoped<IAuditService, AuditService>();

// Idempotent startup seeder for the default reference data. It runs
// once after migrations, never overwrites user edits, and serializes
// concurrent startups with a transaction-scoped PostgreSQL advisory lock.
builder.Services.AddScoped<ReferenceDataSeeder>();

// Departments management (ordered reference data with audit).
builder.Services.AddScoped<DepartmentService>();

// Units and unit aliases, plus the recipe-usage seam consumed by the
// reference-data hard deletes (the other methods consume them).
builder.Services.AddScoped<UnitService>();

// Ingredients and their aliases; the canonical name and every alias
// share one global uniqueness namespace.
builder.Services.AddScoped<IngredientService>();

// Proteins and tags management, plus the recipe-usage seams consumed
// by their Admin-only hard deletes.
builder.Services.AddScoped<ProteinService>();
builder.Services.AddScoped<TagService>();

//The recipe tables now exist, so the usage seam counts and
// reassigns real recipe references instead of the M2 zero-returning stub.
builder.Services.AddScoped<IReferenceUsageChecker, ReferenceDataUsageChecker>();

//Recipe document CRUD, status lifecycle, and nested slices.
builder.Services.AddScoped<RecipeService>();
builder.Services.AddScoped<CalendarService>();

// The Admin-only historical correction of one past date.
builder.Services.AddScoped<HistoricalCorrectionService>();

// The Admin-only irreversible hard recipe delete.
builder.Services.AddScoped<RecipeHardDeleteService>();

// The single-date reroll orchestrator (context build + atomic history).
builder.Services.AddScoped<CalendarRandomizer>();

// The batch reroll preview service (draft-backed sequential engine pass).
builder.Services.AddScoped<BatchRerollService>();

// The pure randomization engine and its injectable secure RNG seam. The
// engine scores candidates and performs a weighted roulette draw; SystemRandomSource
// is the only production source of randomness (docs/RANDOMIZER.md §7 Phase F).
builder.Services.AddSingleton<IRandomSource, SystemRandomSource>();
builder.Services.AddSingleton<RandomizerEngine>();

// Local recipe image storage, content policy, and serving.
builder.Services.AddScoped<RecipeImageService>();

// SSRF-hardened outbound fetcher shared by recipe import and
// the image-from-URL endpoint. Stateless; a single HttpClient with a
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
// Per-user import fetch throttling (30 page / 60 image fetches per
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

// Image-from-URL download orchestration. It shares the SSRF-hardened
// fetcher and the M3 image storage/content policy instead of reimplementing
// either.
builder.Services.AddScoped<RecipeImageImportService>();

// Bound multipart parsing for image uploads before anything is
// buffered; the endpoint enforces the exact 5 MiB file cap and the format
// policy on top of this.
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = RecipeImageService.MaxMultipartBodyBytes;
});

// Pure, dependency-free conversion between compatible units; stateless
// so a singleton. M8 grocery aggregation consumes it via DI.
builder.Services.AddSingleton<UnitConversionService>();

// Pure grocery aggregation over supplied recipe ingredients; stateless and
// I/O-free, so a singleton over the conversion service above.
builder.Services.AddSingleton<GroceryAggregationService>();

// The live grocery list projection over calendar, recipe, manual, and
// check-state rows. Scoped because it reads through the request AppDbContext.
builder.Services.AddScoped<GroceryListService>();

// Range-scoped manual grocery CRUD and the per-user (or system-actor)
// preference store, both scoped over the request AppDbContext.
builder.Services.AddScoped<GroceryManualItemService>();
builder.Services.AddScoped<GroceryPreferenceService>();

//The household staple catalog (add-to-list reuses the manual-item
// create path) and the household-shared check state, scoped over the request
// AppDbContext.
builder.Services.AddScoped<GroceryStapleService>();
builder.Services.AddScoped<GroceryCheckStateService>();

// Read-only statistics aggregates over the request AppDbContext
// on-demand SQL, no cache and no statistics table.
builder.Services.AddScoped<StatisticsService>();

// The bounded, month-grouped meal-history read.
builder.Services.AddScoped<MealHistoryService>();

// The merged, newest-first admin audit read model (audit_events plus
// the reroll/batch calendar_entry_history rows); bounded, read-only.
builder.Services.AddScoped<AuditLogQueryService>();

// The backup-tooling probe. consults the recorded client/server
// major check and refuses a backup when the shipped pg_dump is older than the
// server. Probing never fails application startup.
builder.Services.AddSingleton<IPgDumpVersionReader, PgDumpVersionReader>();
builder.Services.AddScoped<IPgServerVersionReader, NpgsqlServerVersionReader>();
builder.Services.AddScoped<PgToolingInfo>();

// Backup creation, the serialized pg_dump runner, and the
// backup_metadata lifecycle. The runner is
// a seam so orchestration is testable without the client tools; the process
// gate serializes concurrent creates in this instance.
builder.Services.AddSingleton<IPgDumpRunner, PgDumpRunner>();
builder.Services.AddSingleton<BackupOperationGate>();
builder.Services.AddScoped<BackupService>();

// The restore orchestration. The runner and the
// restart/marker/maintenance state are singletons because maintenance mode must
// be visible to every request and there is exactly one restore lane; the
// restore itself opens its own scopes so it can use a fresh DbContext after the
// database has been replaced.
builder.Services.AddSingleton<IPgRestoreRunner, PgRestoreRunner>();
builder.Services.AddSingleton<RestoreState>();
builder.Services.AddSingleton<IRestoreRestartSignal, HostRestartSignal>();
// The swap copies directory contents instead of renaming the live (bind-mount)
// roots; the seam exists so a copy failure is testable (docs/API.md §18).
builder.Services.AddSingleton<IRestoreFileOperations, SystemRestoreFileOperations>();
builder.Services.AddSingleton<RestoreService>();

// The single outbound release-metadata GET. The request carries only a
// neutral User-Agent and the GitHub media type — no token, no body, no
// household data, no identifiers.
builder.Services.AddHttpClient<GitHubReleaseClient>(GitHubReleaseClient.Configure);
builder.Services.AddScoped<UpdateCheckService>();

// Permission model: one uncached grant reader plus the evaluator service
// shared by GET /auth/session and the route enforcement filter.
builder.Services.AddScoped<IPermissionGrantReader, DbPermissionGrantReader>();
builder.Services.AddScoped<PermissionService>();

// Part 2: per-account lockout and progressive delay
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

// Live sync. Server-side SignalR ships in the ASP.NET Core shared
// framework, so no new backend package is added and no backplane is needed
// (one household, one instance). The hub is a server-to-client-only
// notification channel; the bounded inbound frame size and disabled detailed
// errors are the documented guards.
builder.Services
    .AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = LiveSyncHub.MaximumInboundMessageBytes;
        options.EnableDetailedErrors = false;
    })
    .AddJsonProtocol(options =>
    {
        // Live sync uses SignalR's own serializer, which does not inherit the
        // HTTP JSON options set above, so the wire envelope is snake_case here
        // too (docs/API.md §3).
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });
builder.Services.AddSingleton<LiveSyncConnectionRegistry>();
builder.Services.AddSingleton<ILiveSyncPublisher, SignalRLiveSyncPublisher>();

// Argon2id hashing is stateless and thread-safe (libsodium initializes once).
builder.Services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

var app = builder.Build();

AppPaths.EnsureDataDirectories(app.Configuration, app.Logger);

var migrateOnly = args.Contains("--migrate");
if (migrateOnly || app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await DatabaseMigrator.MigrateWithRetryAsync(app.Services, app.Logger);
}

// Seeding runs on every startup, even when automatic migrations are
// disabled, so the reference defaults exist regardless of deployment config.
await ReferenceDataSeeder.SeedAtStartupAsync(app.Services, app.Logger);

if (migrateOnly)
{
    return;
}

// A restore that finished before the last stop wrote a marker; log the
// recorded outcome, verify the restored database is reachable, and clear it
// Idempotent and never fatal at startup.
await RestoreMarker.CompleteStartupAsync(app.Configuration, app.Services, app.Logger);

// The update check runs once in the background (24-hour cache, and no
// call at all when update_check_enabled is false). It is deliberately not
// awaited: a slow or unreachable GitHub must never delay or fail startup.
if (app.Configuration.GetValue("Updates:StartupCheckEnabled", true))
{
    _ = UpdateCheckService.StartStartupCheckAsync(app.Services, app.Logger);
}

// Order matters: the exception handler wraps everything so unexpected failures
// still use the shared error envelope; restore maintenance mode answers
// 503 RESTORE_IN_PROGRESS before any other middleware can touch the database
// the client address (trusted-proxy
// forwarded headers) resolves next so the allowlist, Secure-cookie decisions
// and antiforgery all see the real client; the allowlist then rejects blocked
// clients before any authentication or setup state is consulted; setup gating
// still wins pre-setup (409) for allowed clients; the forced-password-change
// gate runs before CSRF so a flagged session always gets
// PASSWORD_CHANGE_REQUIRED on gated routes.
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<RestoreMaintenanceMiddleware>();
app.UseMiddleware<ClientAddressMiddleware>();
app.UseMiddleware<IpAllowlistMiddleware>();
app.UseAuthentication();
app.UseMiddleware<SetupGatingMiddleware>();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.UseMiddleware<CsrfMiddleware>();
app.UseAuthorization();

app.MapHealthEndpoints();
// The authenticated version display behind Settings → About and the
// shell footer label.
app.MapAboutEndpoints();
// Update status (any authenticated actor) plus the Admin-only forced
// check and enabled preference.
app.MapUpdateEndpoints();
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
app.MapStatisticsEndpoints();
app.MapAdminEndpoints();
app.MapBackupEndpoints();
app.MapRecoveryKeyEndpoints();
app.MapOperationEndpoints();
app.MapImportEndpoints();

// The live-sync hub must be mapped before the SPA catch-all so
// MapFrontend's fallback cannot swallow /hub/live.
app.MapHub<LiveSyncHub>(LiveSyncPaths.HubPath);

app.MapFrontend();

await app.RunAsync();

/// <summary>Entry point marker used by the integration test host.</summary>
public partial class Program;
