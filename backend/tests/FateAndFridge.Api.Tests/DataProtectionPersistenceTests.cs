using System.Net;
using System.Text;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Simulates a container replacement: two hosts share one persistent data
/// directory and one database, so a session and antiforgery token issued by the
/// first must resolve on the second (docs/MILESTONES.md M1 decisions). Requires
/// a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class DataProtectionPersistenceTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset SeedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Session_and_antiforgery_token_survive_a_host_replacement()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHouseholdAsync(cancellationToken);
        await MarkSetupCompletedAsync(cancellationToken);

        var dataDir = CreateTempDataDir();
        try
        {
            // Pre-create the key directory with broader permissions so the test
            // verifies the app tightens an existing directory, not just a fresh
            // one.
            var keyDirectory = Path.Combine(dataDir, "config", "dp-keys");
            Directory.CreateDirectory(keyDirectory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    keyDirectory,
                    UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead
                        | UnixFileMode.OtherExecute);
            }

            using var firstHost = fixture.CreateFactory(dataDir);
            using var secondHost = fixture.CreateFactory(dataDir);

            var cookies = new CookieContainer();
            var firstClient = firstHost.CreateDefaultClient(new CookieContainerHandler(cookies));
            var secondClient = secondHost.CreateDefaultClient(new CookieContainerHandler(cookies));

            var user = await SeedUserAsync(cancellationToken);
            using (var scope = firstHost.Services.CreateScope())
            {
                var (_, rawToken) = await scope.ServiceProvider
                    .GetRequiredService<SessionService>()
                    .CreateAsync(user.Id, "127.0.0.1", "dp-test", cancellationToken);
                cookies.Add(
                    new Uri("http://localhost/"),
                    new Cookie(SessionAuthenticationDefaults.CookieName, rawToken) { Path = "/" });
            }

            // Host 1 resolves the session and issues the antiforgery token.
            var bootstrap = await firstClient.GetAsync("/api/v1/settings/household", cancellationToken);
            bootstrap.StatusCode.Should().Be(HttpStatusCode.OK);
            bootstrap.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var csrfValues)
                .Should().BeTrue();
            var csrfToken = csrfValues!.Single();

            // The key ring is persisted on the mounted data directory.
            Directory.Exists(keyDirectory).Should().BeTrue();
            Directory.GetFiles(keyDirectory, "key-*.xml").Should().NotBeEmpty();

            // The key directory is owner-only on Unix (decision: mode 0700).
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(keyDirectory).Should().Be(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Host 2 validates the same session cookie and token.
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household")
            {
                Content = new StringContent(
                    """{"household_name": "Survived Restart"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);

            var response = await secondClient.SendAsync(request, cancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var context = fixture.CreateContext();
            var householdName = await context.HouseholdSettings
                .AsNoTracking()
                .Select(settings => settings.HouseholdName)
                .SingleAsync(cancellationToken);
            householdName.Should().Be("Survived Restart");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private async Task SeedHouseholdAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "Test Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.Multiuser,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<User> SeedUserAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = "admin",
            NormalizedUsername = "admin",
            PasswordHash = "phc-placeholder",
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user;
    }

    private async Task MarkSetupCompletedAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO application_settings (key, value_json, updated_at)
            VALUES ({SetupStateService.SetupCompletedKey}, 'true'::jsonb, {DateTimeOffset.UtcNow})
            ON CONFLICT (key) DO UPDATE
            SET value_json = 'true'::jsonb, updated_at = EXCLUDED.updated_at
            """,
            cancellationToken);
    }

    private static string CreateTempDataDir() =>
        Path.Combine(Path.GetTempPath(), "fate-and-fridge-tests", Guid.NewGuid().ToString("N"));
}
