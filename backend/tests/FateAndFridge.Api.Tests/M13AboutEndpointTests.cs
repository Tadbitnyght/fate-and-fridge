using System.Data.Common;
using System.Net;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M13-08 version display (docs/M13_PLAN.md §1.12, §2.2, §4; docs/API.md §24).
/// Requires a Docker daemon.
/// <para>
/// <c>GET /api/v1/about</c> is readable by every authenticated actor — Admin,
/// member, and the single-user system actor — and returns the real
/// <see cref="AppInfo.Version"/> plus the last applied migration. A database
/// that cannot be read degrades to <c>schema_version: null</c> with a logged
/// warning; the route still answers <c>200</c>.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(M13BackupCollection.Name)]
public sealed class M13AboutEndpointTests(M13BackupFixture fixture) : IAsyncLifetime
{
    private const string AboutPath = "/api/v1/about";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m13-about-test";

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task About_returns_the_real_app_version_and_last_applied_migration_for_admins()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);

        using var factory = CreateFactory(dataDir);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await session.Client.GetAsync(AboutPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadJsonAsync(response);

        var appVersion = payload.GetProperty("app_version").GetString();
        appVersion.Should().Be(AppInfo.Version);
        appVersion.Should().Be("1.0.0", "the 1.0.0 release is the shipped product version");
        appVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+$", "the contract is SemVer only");
        appVersion.Should().NotContain("+", "no build hash is exposed");

        var schemaVersion = payload.GetProperty("schema_version").GetString();
        schemaVersion.Should().NotBeNullOrEmpty();

        await using var context = fixture.CreateContext();
        var applied = await context.Database.GetAppliedMigrationsAsync(Ct);
        applied.Should().NotBeEmpty();
        schemaVersion.Should().Be(applied.Last());
    }

    [Fact]
    public async Task About_requires_authentication()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();

        using var factory = CreateFactory(dataDir);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(AboutPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ReadErrorCodeAsync(response)).Should().Be("AUTH_REQUIRED");
    }

    [Fact]
    public async Task About_is_readable_by_a_member_without_any_permission_key()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedMultiuserHouseholdAsync();
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);

        using var factory = CreateFactory(dataDir);
        using var member = await SignInAsync(factory, "member", MemberPassword);

        var response = await member.Client.GetAsync(AboutPath, Ct);

        // No admin gate and no eighth permission key: version display is for
        // every signed-in household member (docs/M13_PLAN.md §1.12).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("app_version").GetString().Should().Be(AppInfo.Version);
        payload.GetProperty("schema_version").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task About_is_readable_by_the_single_user_system_actor()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedSingleUserHouseholdAsync();

        using var factory = CreateFactory(dataDir);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(AboutPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("app_version").GetString().Should().Be(AppInfo.Version);
        payload.GetProperty("schema_version").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task About_stays_200_with_a_null_schema_version_and_a_warning_when_the_database_is_unreadable()
    {
        var dataDir = fixture.NewDataDirectory();
        await SeedSingleUserHouseholdAsync();

        var recorder = new RecordingLoggerProvider();
        using var factory = CreateFactory(
            dataDir,
            services =>
            {
                services.AddSingleton<ILoggerProvider>(recorder);

                // Program registers AppDbContext against the fixture's
                // connection string; rebuild it so every request-scoped context
                // fails on the migration-history read only. Everything else
                // (setup gating, the session actor) keeps working, which is the
                // "database unreadable for the version" seam.
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options
                    .UseNpgsql(fixture.ConnectionString)
                    .UseSnakeCaseNamingConvention()
                    .AddInterceptors(new MigrationHistoryFailureInterceptor()));
            });

        using var client = factory.CreateClient();

        var response = await client.GetAsync(AboutPath, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("app_version").GetString().Should().Be(AppInfo.Version);
        payload.GetProperty("schema_version").ValueKind.Should().Be(JsonValueKind.Null);

        recorder.Messages.Should().Contain(
            message => message.Contains(AboutPath, StringComparison.Ordinal),
            "an unreadable schema version must be recorded, never silently swallowed");
    }

    private ApiFactory CreateFactory(string dataDir, Action<IServiceCollection>? configureServices = null) =>
        fixture.CreateFactory(dataDir, configureServices);

    private async Task SeedMultiuserHouseholdAsync()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await MarkSetupCompletedAsync();
    }

    private async Task SeedSingleUserHouseholdAsync()
    {
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
    }

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M13 About Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = authMode,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task MarkSetupCompletedAsync()
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {SeedTimestamp})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            Ct);
    }

    private async Task SeedUserAsync(string username, string password, UserRole role)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);
    }

    private static async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password)
    {
        var cookies = new CookieContainerHandler(new CookieContainer());
        var client = factory.CreateDefaultClient(cookies);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);

        var login = await client.SendAsync(request, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        return new SignedInClient(client);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>
    /// Fails the EF migration-history read so the About degraded path can be
    /// exercised without stopping PostgreSQL for the shared fixture.
    /// </summary>
    private sealed class MigrationHistoryFailureInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowIfMigrationHistory(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowIfMigrationHistory(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowIfMigrationHistory(DbCommand command)
        {
            if (command.CommandText.Contains("__EFMigrationsHistory", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated unreadable migration history.");
            }
        }
    }

    private sealed record SignedInClient(HttpClient Client) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
