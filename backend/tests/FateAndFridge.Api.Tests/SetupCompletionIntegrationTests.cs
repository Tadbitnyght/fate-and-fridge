using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Auth.Permissions;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of <c>POST /setup/complete</c> (M1-04,
/// docs/API.md §21): single-shot atomic completion, first-Admin session
/// establishment, recovery key file handling, concurrent completion, rollback,
/// and secret hygiene. Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SetupCollection.Name)]
public sealed class SetupCompletionIntegrationTests(M1SetupFixture fixture) : IAsyncLifetime
{
    private const string AdminPassword = "correct horse battery staple";

    private readonly List<string> _tempDataDirs = [];

    public ValueTask InitializeAsync() =>
        new(fixture.ResetAsync(TestContext.Current.CancellationToken));

    public ValueTask DisposeAsync()
    {
        foreach (var directory in _tempDataDirs)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Single_user_completion_persists_state_lifts_gating_and_creates_no_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        var response = await CompleteSetupAsync(client, SingleUserBody(), cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var root = await ReadJsonAsync(response, cancellationToken);
        root.EnumerateObject().Select(property => property.Name)
            .Should().Equal("auth_mode", "recovery_key");
        root.GetProperty("auth_mode").GetString().Should().Be("single_user");

        var recoveryKey = root.GetProperty("recovery_key").GetString()!;
        Base64Url.DecodeFromChars(recoveryKey).Should().HaveCount(RecoveryKeyStore.KeyLengthBytes);

        var keyPath = Path.Combine(dataDir, "config", RecoveryKeyStore.FileName);
        File.Exists(keyPath).Should().BeTrue();
        (await File.ReadAllTextAsync(keyPath, cancellationToken)).Trim().Should().Be(recoveryKey);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(keyPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await using (var context = fixture.CreateContext())
        {
            var household = await context.HouseholdSettings.AsNoTracking().SingleAsync(cancellationToken);
            household.Id.Should().Be(1);
            household.HouseholdName.Should().Be("The Smiths");
            household.Timezone.Should().Be("America/Chicago");
            household.WeekStartsOn.Should().Be(0);
            household.DefaultDinerCount.Should().Be(4);
            household.StartupPage.Should().Be(StartupPage.Calendar);
            household.AuthMode.Should().Be(AuthMode.SingleUser);

            (await context.Users.CountAsync(cancellationToken)).Should().Be(0);

            var settings = await context.ApplicationSettings
                .AsNoTracking()
                .ToDictionaryAsync(setting => setting.Key, setting => setting.ValueJson, cancellationToken);
            settings[SetupStateService.SetupCompletedKey].Should().Be("true");
            settings[SetupCompletionService.UpdateCheckEnabledKey].Should().Be("true");
            settings[SetupCompletionService.IpAllowlistEnabledKey].Should().Be("false");
            using var ranges = JsonDocument.Parse(settings[SetupCompletionService.IpAllowlistRangesKey]!);
            ranges.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            ranges.RootElement.GetArrayLength().Should().Be(0);
            using var proxies = JsonDocument.Parse(settings[SetupCompletionService.TrustedProxiesKey]!);
            proxies.RootElement.GetArrayLength().Should().Be(0);
        }

        var statusResponse = await client.GetAsync("/api/v1/setup/status", cancellationToken);
        var status = await ReadJsonAsync(statusResponse, cancellationToken);
        status.GetProperty("setup_completed").GetBoolean().Should().BeTrue();
        status.GetProperty("auth_mode").GetString().Should().Be("single_user");
        status.GetProperty("recovery_key_present").GetBoolean().Should().BeTrue();

        // Gating is lifted: the single-user system actor reaches protected routes.
        (await client.GetAsync("/api/v1/settings/household", cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Multiuser_completion_creates_the_first_admin_with_a_usable_session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);
        var cookies = new CookieContainer();
        using var client = factory.CreateDefaultClient(new CookieContainerHandler(cookies));

        var response = await CompleteSetupAsync(client, MultiuserBody(), cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var root = await ReadJsonAsync(response, cancellationToken);
        root.EnumerateObject().Select(property => property.Name)
            .Should().Equal("auth_mode", "recovery_key", "session");
        root.GetProperty("auth_mode").GetString().Should().Be("multiuser");

        var session = root.GetProperty("session");
        session.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        session.GetProperty("auth_mode").GetString().Should().Be("multiuser");
        session.GetProperty("must_change_password").GetBoolean().Should().BeFalse();
        session.GetProperty("user").GetProperty("username").GetString().Should().Be("smith");
        session.GetProperty("user").GetProperty("role").GetString().Should().Be("admin");
        session.GetProperty("permissions").EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal(PermissionKeys.All);

        var setCookies = response.Headers.GetValues("Set-Cookie").ToArray();
        setCookies.Should().Contain(header => header.StartsWith(
            $"{SessionAuthenticationDefaults.CookieName}=",
            StringComparison.Ordinal));
        setCookies.Should().Contain(header => header.StartsWith("faf_csrf=", StringComparison.Ordinal));
        response.Headers.TryGetValues(CsrfMiddleware.HeaderName, out var csrfValues).Should().BeTrue();
        var csrfToken = csrfValues!.Single();

        long adminId;
        await using (var context = fixture.CreateContext())
        {
            var admin = await context.Users.AsNoTracking().SingleAsync(cancellationToken);
            admin.Username.Should().Be("smith");
            admin.NormalizedUsername.Should().Be("smith");
            admin.Role.Should().Be(UserRole.Admin);
            admin.IsActive.Should().BeTrue();
            admin.MustChangePassword.Should().BeFalse();
            admin.LastLoginAt.Should().NotBeNull("setup signs the first Admin in");
            admin.LastLoginAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
            new Argon2idPasswordHasher().Verify(admin.PasswordHash, AdminPassword).Should().BeTrue();
            adminId = admin.Id;

            (await context.UserSessions.CountAsync(cancellationToken)).Should().Be(1);
        }

        // The setup session is fully usable for an Admin-only route.
        using (var patchRequest = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings/household"))
        {
            patchRequest.Content = new StringContent(
                """{"household_name": "Renamed"}""",
                Encoding.UTF8,
                "application/json");
            patchRequest.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
            (await client.SendAsync(patchRequest, cancellationToken)).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        // The same credentials work through the normal login flow.
        using var anonymousClient = factory.CreateDefaultClient();
        var login = await LoginAsync(anonymousClient, "smith", AdminPassword, cancellationToken);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginRoot = await ReadJsonAsync(login, cancellationToken);
        loginRoot.GetProperty("user").GetProperty("id").GetInt64().Should().Be(adminId);
        loginRoot.GetProperty("user").GetProperty("role").GetString().Should().Be("admin");
    }

    [Fact]
    public async Task An_existing_recovery_key_file_is_reused_validated_and_tightened()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        var keyPath = Path.Combine(dataDir, "config", RecoveryKeyStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        var existingKey = Base64Url.EncodeToString(
            RandomNumberGenerator.GetBytes(RecoveryKeyStore.KeyLengthBytes));
        await File.WriteAllTextAsync(keyPath, existingKey, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                keyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        var response = await CompleteSetupAsync(client, SingleUserBody(), cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadJsonAsync(response, cancellationToken)).GetProperty("recovery_key").GetString()
            .Should().Be(existingKey);
        (await File.ReadAllTextAsync(keyPath, cancellationToken)).Should().Be(existingKey);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(keyPath).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task An_invalid_existing_recovery_key_fails_with_a_500_envelope_and_no_state()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        var keyPath = Path.Combine(dataDir, "config", RecoveryKeyStore.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        const string invalidKey = "not-a-valid-recovery-key";
        await File.WriteAllTextAsync(keyPath, invalidKey, cancellationToken);

        var recorder = new RecordingLoggerProvider();
        using var factory = fixture.CreateFactory(dataDir).WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(recorder);
            }));
        using var client = factory.CreateClient();

        var response = await CompleteSetupAsync(client, SingleUserBody(), cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.InternalError);
        var message = error.GetProperty("message").GetString()!;
        message.Should().Contain(keyPath);
        message.Should().Contain("base64url");
        message.Should().NotContain(invalidKey, "the invalid file content must never be echoed");

        // Nothing was committed and the file was not overwritten.
        await using (var context = fixture.CreateContext())
        {
            (await context.HouseholdSettings.CountAsync(cancellationToken)).Should().Be(0);
            (await context.ApplicationSettings.CountAsync(
                setting => setting.Key != ReferenceDataSeeder.SeededKey, cancellationToken)).Should().Be(0);
            (await context.Users.CountAsync(cancellationToken)).Should().Be(0);
        }

        (await File.ReadAllTextAsync(keyPath, cancellationToken)).Should().Be(invalidKey);

        // The operator-facing log names the path and expected format without
        // echoing key bytes.
        recorder.AllText.Should().Contain(text => text.Contains(keyPath, StringComparison.Ordinal));
        recorder.AllText.Should().Contain(text => text.Contains("base64url", StringComparison.Ordinal));
        recorder.AllText.Should().NotContain(text => text.Contains(invalidKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeated_completion_returns_409_without_side_effects()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        var first = await CompleteSetupAsync(client, MultiuserBody(), cancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstKey = (await ReadJsonAsync(first, cancellationToken)).GetProperty("recovery_key").GetString();

        var second = await CompleteSetupAsync(client, MultiuserBody(), cancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(second, cancellationToken)).Should().Be(ErrorCodes.SetupAlreadyCompleted);

        await using var context = fixture.CreateContext();
        (await context.HouseholdSettings.CountAsync(cancellationToken)).Should().Be(1);
        (await context.Users.CountAsync(cancellationToken)).Should().Be(1);
        (await context.UserSessions.CountAsync(cancellationToken)).Should().Be(1);
        (await File.ReadAllTextAsync(
            Path.Combine(dataDir, "config", RecoveryKeyStore.FileName),
            cancellationToken)).Trim().Should().Be(firstKey);
    }

    [Fact]
    public async Task Repeated_completion_with_a_malformed_body_still_returns_409()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        (await CompleteSetupAsync(client, MultiuserBody(), cancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.Created);

        // The repeat check runs before body parsing/validation.
        var repeat = await CompleteSetupAsync(client, "not json at all", cancellationToken);

        repeat.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(repeat, cancellationToken)).Should().Be(ErrorCodes.SetupAlreadyCompleted);
    }

    [Fact]
    public async Task Concurrent_completions_allow_exactly_one_success()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            CompleteSetupAsync(client, SingleUserBody(), cancellationToken)));

        try
        {
            responses.Count(response => response.StatusCode == HttpStatusCode.Created).Should().Be(1);
            responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).Should().Be(4);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using var context = fixture.CreateContext();
        (await context.HouseholdSettings.CountAsync(cancellationToken)).Should().Be(1);
        (await context.Users.CountAsync(cancellationToken)).Should().Be(0);
        (await context.ApplicationSettings
                .CountAsync(setting => setting.Key == SetupStateService.SetupCompletedKey, cancellationToken))
            .Should().Be(1);

        var statusResponse = await client.GetAsync("/api/v1/setup/status", cancellationToken);
        var status = await ReadJsonAsync(statusResponse, cancellationToken);
        status.GetProperty("setup_completed").GetBoolean().Should().BeTrue();
        status.GetProperty("recovery_key_present").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Completion_fails_gracefully_when_the_config_path_is_a_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        // Replace the config directory with a file so key-file creation fails
        // after the database writes, forcing the transaction to roll back.
        var configPath = Path.Combine(dataDir, "config");
        Directory.Delete(configPath, recursive: true);
        await File.WriteAllTextAsync(configPath, "blocked", cancellationToken);

        var response = await CompleteSetupAsync(client, SingleUserBody(), cancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.InternalError);
        error.GetProperty("message").GetString().Should()
            .Contain(RecoveryKeyStore.FileName)
            .And.Contain("could not be created");

        await using (var context = fixture.CreateContext())
        {
            (await context.HouseholdSettings.CountAsync(cancellationToken)).Should().Be(0);
            (await context.Users.CountAsync(cancellationToken)).Should().Be(0);
            (await context.ApplicationSettings.CountAsync(
                setting => setting.Key != ReferenceDataSeeder.SeededKey, cancellationToken)).Should().Be(0);
        }

        var statusResponse = await client.GetAsync("/api/v1/setup/status", cancellationToken);
        (await ReadJsonAsync(statusResponse, cancellationToken))
            .GetProperty("setup_completed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Completion_fails_gracefully_when_the_config_directory_is_read_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        var configPath = Path.Combine(dataDir, "config");
        Directory.CreateDirectory(configPath);

        using var factory = fixture.CreateFactory(dataDir);
        using var client = factory.CreateClient();

        // No key file exists; making the directory read-only blocks creation.
        File.SetUnixFileMode(
            configPath,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var response = await CompleteSetupAsync(client, SingleUserBody(), cancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var root = await ReadJsonAsync(response, cancellationToken);
            var error = root.GetProperty("error");
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.InternalError);
            error.GetProperty("message").GetString().Should()
                .Contain(RecoveryKeyStore.FileName)
                .And.Contain("could not be created");

            await using var context = fixture.CreateContext();
            (await context.HouseholdSettings.CountAsync(cancellationToken)).Should().Be(0);
            (await context.ApplicationSettings.CountAsync(
                setting => setting.Key != ReferenceDataSeeder.SeededKey, cancellationToken)).Should().Be(0);
        }
        finally
        {
            File.SetUnixFileMode(
                configPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task Invalid_requests_return_400_without_writing_anything()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var factory = fixture.CreateFactory(CreateTempDataDir());
        using var client = factory.CreateClient();

        var bodies = new[]
        {
            "not json at all",
            MultiuserBody(includeAdmin: false),
            MultiuserBody(ipAllowlistRange: "not-a-range", ipAllowlistEnabled: true),
        };

        foreach (var body in bodies)
        {
            var response = await CompleteSetupAsync(client, body, cancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var root = await ReadJsonAsync(response, cancellationToken);
            var error = root.GetProperty("error");
            error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
            error.GetProperty("details").GetProperty("field_errors").EnumerateObject().Should().NotBeEmpty();
        }

        await using var context = fixture.CreateContext();
        (await context.HouseholdSettings.CountAsync(cancellationToken)).Should().Be(0);
        (await context.ApplicationSettings.CountAsync(
            setting => setting.Key != ReferenceDataSeeder.SeededKey, cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Recovery_key_password_and_hash_never_reach_logs_or_the_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        var recorder = new RecordingLoggerProvider();
        using var factory = fixture.CreateFactory(dataDir).WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(recorder);
            }));
        using var client = factory.CreateClient();

        var response = await CompleteSetupAsync(client, MultiuserBody(), cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var recoveryKey = (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("recovery_key").GetString()!;

        string passwordHash;
        await using (var context = fixture.CreateContext())
        {
            passwordHash = await context.Users
                .AsNoTracking()
                .Select(user => user.PasswordHash)
                .SingleAsync(cancellationToken);

            var storedSettings = string.Join(
                '\n',
                await context.ApplicationSettings
                    .AsNoTracking()
                    .Select(setting => setting.ValueJson)
                    .ToListAsync(cancellationToken));
            storedSettings.Should().NotContain(recoveryKey);
        }

        passwordHash.Should().NotBe(recoveryKey);
        recorder.Messages.Should().NotBeEmpty();
        foreach (var secret in new[] { AdminPassword, recoveryKey, passwordHash })
        {
            recorder.AllText.Should().NotContain(
                text => text.Contains(secret, StringComparison.Ordinal),
                "setup secrets must never reach logs");
        }
    }

    [Fact]
    public async Task Setup_enabling_the_allowlist_requires_caller_coverage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = CreateFactoryWithClientAddress(dataDir);
        using var client = factory.CreateClient();

        var response = await CompleteSetupAsync(
            client,
            MultiuserBody(ipAllowlistEnabled: true, ipAllowlistRange: "10.0.0.0/8"),
            cancellationToken,
            clientIp: "203.0.113.10");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var root = await ReadJsonAsync(response, cancellationToken);
        var error = root.GetProperty("error");
        error.GetProperty("code").GetString().Should().Be(ErrorCodes.ValidationFailed);
        error.GetProperty("details").GetProperty("field_errors")
            .TryGetProperty("ip_allowlist.ranges", out _).Should().BeTrue();

        // Nothing was written and setup is still incomplete.
        await using (var context = fixture.CreateContext())
        {
            (await context.HouseholdSettings.CountAsync(cancellationToken)).Should().Be(0);
            (await context.Users.CountAsync(cancellationToken)).Should().Be(0);
            (await context.ApplicationSettings.CountAsync(
                setting => setting.Key != ReferenceDataSeeder.SeededKey, cancellationToken)).Should().Be(0);
        }

        var status = await client.GetAsync("/api/v1/setup/status", cancellationToken);
        (await ReadJsonAsync(status, cancellationToken)).GetProperty("setup_completed").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public async Task Setup_enabling_the_allowlist_with_a_covering_range_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = CreateFactoryWithClientAddress(dataDir);
        using var client = factory.CreateClient();

        var response = await CompleteSetupAsync(
            client,
            MultiuserBody(ipAllowlistEnabled: true, ipAllowlistRange: "203.0.113.0/24"),
            cancellationToken,
            clientIp: "203.0.113.10");

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        await using var context = fixture.CreateContext();
        var settings = await context.ApplicationSettings
            .AsNoTracking()
            .ToDictionaryAsync(setting => setting.Key, setting => setting.ValueJson, cancellationToken);
        settings[SetupCompletionService.IpAllowlistEnabledKey].Should().Be("true");
        using var ranges = JsonDocument.Parse(settings[SetupCompletionService.IpAllowlistRangesKey]!);
        ranges.RootElement.EnumerateArray().Select(element => element.GetString()!)
            .Should().Equal("203.0.113.0/24");
    }

    [Fact]
    public async Task Setup_enabling_the_allowlist_from_loopback_succeeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dataDir = CreateTempDataDir();
        using var factory = CreateFactoryWithClientAddress(dataDir);
        using var client = factory.CreateClient();

        var response = await CompleteSetupAsync(
            client,
            MultiuserBody(ipAllowlistEnabled: true, ipAllowlistRange: "10.0.0.0/8"),
            cancellationToken,
            clientIp: "127.0.0.1");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private WebApplicationFactory<Program> CreateFactoryWithClientAddress(string dataDir) =>
        fixture.CreateFactory(dataDir).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new TestRemoteIpStartupFilter())));

    private string CreateTempDataDir()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "fate-and-fridge-tests",
            Guid.NewGuid().ToString("N"));
        _tempDataDirs.Add(directory);
        return directory;
    }

    private static string MultiuserBody(
        bool includeAdmin = true,
        bool ipAllowlistEnabled = false,
        string ipAllowlistRange = "")
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["household_name"] = "The Smiths",
            ["timezone"] = "America/Chicago",
            ["week_starts_on"] = 0,
            ["default_diner_count"] = 4,
            ["startup_page"] = "calendar",
            ["auth_mode"] = "multiuser",
            ["ip_allowlist"] = new
            {
                enabled = ipAllowlistEnabled,
                ranges = ipAllowlistEnabled ? new[] { ipAllowlistRange } : [],
            },
            ["update_check_enabled"] = true,
        };

        if (includeAdmin)
        {
            body["admin"] = new { username = "smith", password = AdminPassword };
        }

        return JsonSerializer.Serialize(body);
    }

    private static string SingleUserBody() => JsonSerializer.Serialize(new
    {
        household_name = "The Smiths",
        timezone = "America/Chicago",
        week_starts_on = 0,
        default_diner_count = 4,
        startup_page = "calendar",
        auth_mode = "single_user",
        ip_allowlist = new { enabled = false, ranges = Array.Empty<string>() },
        update_check_enabled = true,
    });

    private static async Task<HttpResponseMessage> CompleteSetupAsync(
        HttpClient client,
        string json,
        CancellationToken cancellationToken,
        string? clientIp = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/setup/complete")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (clientIp is not null)
        {
            request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, clientIp);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<HttpResponseMessage> LoginAsync(
        HttpClient client,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { username, password }),
                Encoding.UTF8,
                "application/json"),
        };

        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonElement> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return payload.RootElement.Clone();
    }

    private static async Task<string> ReadErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken) =>
        (await ReadJsonAsync(response, cancellationToken))
            .GetProperty("error").GetProperty("code").GetString()!;
}
