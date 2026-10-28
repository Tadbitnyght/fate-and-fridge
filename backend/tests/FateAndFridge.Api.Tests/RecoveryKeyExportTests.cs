using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Auth;
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

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Admin recovery-key export. Requires a Docker daemon.
/// <para>
/// The battery covers the permission matrix, the typed <c>EXPORT</c> gate, the
/// missing/unusable key file, the attachment-only response, and the audit row.
/// Every audit string field is asserted not to contain the key: the key must
/// never appear in an audit payload, log, or error.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(BackupCollection.Name)]
public sealed class RecoveryKeyExportTests(BackupFixture fixture) : IAsyncLifetime
{
    private const string ExportPath = "/api/v1/admin/recovery-key/export";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m13-recovery-key-export-test";
    private const string TestIp = "203.0.113.23";
    private const string Reason = "Keep a copy in the safe before the upgrade";

    private static readonly byte[] RecoveryKey =
        Enumerable.Range(11, 32).Select(index => (byte)index).ToArray();

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Export_returns_the_key_as_an_attachment_and_audits_without_the_key()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        var admin = await SeedAdminHouseholdAsync();

        using var factory = CreateFactory(dataDir);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await ExportAsync(session.Client, session.CsrfToken, confirmation: "EXPORT");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");

        var disposition = response.Content.Headers.GetValues("Content-Disposition").Single();
        disposition.Should().StartWith("attachment;");
        disposition.Should().Contain("recovery.key");

        // A credential-bearing download must never be stored by a browser or
        // an intermediary.
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();

        var keyText = Base64Url.EncodeToString(RecoveryKey);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be(keyText);

        var audits = await ReadExportAuditRowsAsync();
        audits.Should().ContainSingle();
        var audit = audits[0];
        audit.EventType.Should().Be(AuditEventTypes.RecoveryKeyExported);
        audit.EventType.Should().Be("recovery_key.exported");
        audit.EntityType.Should().Be(AuditEntityTypes.RecoveryKey);
        audit.EntityId.Should().BeNull();
        audit.Reason.Should().Be(Reason);
        audit.ActorUserId.Should().Be(admin.Id);
        audit.IpAddress.Should().Be(TestIp);
        audit.BeforeJson.Should().BeNull();
        audit.AfterJson.Should().BeNull();

        // The key must never leak into any audit row, not even inside metadata.
        foreach (var row in await ReadAuditRowsAsync())
        {
            foreach (var value in new[]
                     {
                         row.EventType,
                         row.EntityType,
                         row.EntityId,
                         row.BeforeJson,
                         row.AfterJson,
                         row.Reason,
                         row.IpAddress,
                         row.MetadataJson,
                     })
            {
                value.Should().NotContain(
                    keyText,
                    "the recovery key must never appear in an audit payload");
            }
        }
    }

    [Fact]
    public async Task Export_is_refused_without_the_exact_typed_confirmation()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        await SeedAdminHouseholdAsync();

        using var factory = CreateFactory(dataDir);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await ExportAsync(session.Client, session.CsrfToken, confirmation: "export");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.ConfirmationMismatch);
        (await ReadExportAuditRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Export_is_refused_without_a_reason()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        await SeedAdminHouseholdAsync();

        using var factory = CreateFactory(dataDir);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            ExportPath,
            """{"confirmation":"EXPORT"}""",
            session.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.ReasonRequired);
        (await ReadExportAuditRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_recovery_key_is_reported_and_no_key_file_is_created()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: false);
        await SeedAdminHouseholdAsync();

        using var factory = CreateFactory(dataDir);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await ExportAsync(session.Client, session.CsrfToken, confirmation: "EXPORT");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.BackupKeyMissing);
        File.Exists(Path.Combine(dataDir, "config", RecoveryKeyStore.FileName)).Should().BeFalse(
            "the export never creates a recovery key");
        (await ReadExportAuditRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unusable_recovery_key_is_refused_without_echoing_its_content()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: false);
        const string garbage = "this-is-not-a-recovery-key-and-must-never-be-echoed";
        await File.WriteAllTextAsync(
            Path.Combine(dataDir, "config", RecoveryKeyStore.FileName),
            garbage,
            Ct);
        await SeedAdminHouseholdAsync();

        using var factory = CreateFactory(dataDir);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var response = await ExportAsync(session.Client, session.CsrfToken, confirmation: "EXPORT");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var payload = await response.Content.ReadAsStringAsync(Ct);
        payload.Should().Contain(ErrorCodes.BackupKeyMissing);
        payload.Should().NotContain(garbage);
        (await ReadExportAuditRowsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_caller_must_be_an_admin_and_the_single_user_actor_is_an_admin()
    {
        await fixture.ResetAsync(Ct);
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        await SeedAdminHouseholdAsync();
        await SeedUserAsync("member", MemberPassword, UserRole.Member);

        using var factory = CreateFactory(dataDir);

        using (var anonymous = factory.CreateClient())
        {
            var response = await SendJsonAsync(
                anonymous,
                HttpMethod.Post,
                ExportPath,
                """{"reason":"anonymous","confirmation":"EXPORT"}""",
                csrfToken: null);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        using (var member = await SignInAsync(factory, "member", MemberPassword))
        {
            var response = await ExportAsync(member.Client, member.CsrfToken, confirmation: "EXPORT");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        using (var admin = await SignInAsync(factory, "root", AdminPassword))
        {
            var response = await ExportAsync(admin.Client, admin.CsrfToken, confirmation: "EXPORT");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // The single-user system actor holds every permission.
        await fixture.ResetAsync(Ct);
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();
        using var singleUserFactory = CreateFactory(dataDir);
        using var client = singleUserFactory.CreateClient();

        var singleUserResponse = await SendJsonAsync(
            client,
            HttpMethod.Post,
            ExportPath,
            """{"reason":"single-user export","confirmation":"EXPORT"}""",
            csrfToken: null);
        singleUserResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await singleUserResponse.Content.ReadAsStringAsync(Ct)).Should()
            .Be(Base64Url.EncodeToString(RecoveryKey));
    }

    private async Task<User> SeedAdminHouseholdAsync()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await MarkSetupCompletedAsync();
        return await SeedUserAsync("root", AdminPassword, UserRole.Admin);
    }

    private ApiFactory CreateFactory(string dataDir) =>
        fixture.CreateFactory(dataDir, services =>
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>());

    private string SeedDataDirectory(bool withRecoveryKey)
    {
        var dataDir = fixture.NewDataDirectory();
        Directory.CreateDirectory(Path.Combine(dataDir, "config"));

        if (withRecoveryKey)
        {
            File.WriteAllText(
                Path.Combine(dataDir, "config", RecoveryKeyStore.FileName),
                Base64Url.EncodeToString(RecoveryKey));
        }

        return dataDir;
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(Ct);
    }

    private async Task<List<AuditEvent>> ReadExportAuditRowsAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents
            .AsNoTracking()
            .Where(row => row.EventType == AuditEventTypes.RecoveryKeyExported)
            .OrderBy(row => row.Id)
            .ToListAsync(Ct);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M13 Recovery Key Household",
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

    private async Task<User> SeedUserAsync(string username, string password, UserRole role)
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
        return user;
    }

    private static Task<HttpResponseMessage> ExportAsync(
        HttpClient client,
        string? csrfToken,
        string confirmation) =>
        SendJsonAsync(
            client,
            HttpMethod.Post,
            ExportPath,
            JsonSerializer.Serialize(new { reason = Reason, confirmation }),
            csrfToken);

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
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", TestUserAgent);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);

        var login = await client.SendAsync(request, Ct);
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        return new SignedInClient(client, login.Headers.GetValues(CsrfMiddleware.HeaderName).Single());
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string? csrfToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (csrfToken is not null)
        {
            request.Headers.Add(CsrfMiddleware.HeaderName, csrfToken);
        }

        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);
        return await client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString()!;

    private sealed record SignedInClient(HttpClient Client, string CsrfToken) : IDisposable
    {
        public void Dispose() => Client.Dispose();
    }
}
