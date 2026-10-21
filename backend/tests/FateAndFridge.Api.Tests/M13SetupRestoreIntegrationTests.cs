using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FateAndFridge.Api.Backup;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Endpoints;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M13-05 clean-install restore (docs/M13_PLAN.md §1.10, §2.2, §4;
/// docs/API.md §21): <c>POST /api/v1/setup/restore</c> is reachable **only
/// before setup**, takes the archive plus a pasted recovery key, reuses the
/// M13-04 validation/safety-backup sequence, and restarts into the restored
/// state. Requires a Docker daemon (Testcontainers PostgreSQL).
/// <para>
/// The orchestration runs against the same fakes as the M13-04 suite: no
/// PostgreSQL client tools exist on the development workstation, so the real
/// <c>pg_restore</c> path stays a skipped test and the M13-10 image gate.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(M13BackupCollection.Name)]
public sealed class M13SetupRestoreIntegrationTests(M13BackupFixture fixture) : IAsyncLifetime
{
    private const string SetupRestorePath = "/api/v1/setup/restore";
    private const string PgDump17 = "pg_dump (PostgreSQL) 17.4 (Debian 17.4-1.pgdg120+1)";
    private const string TestIp = "203.0.113.47";
    private const string ArchiveFileName = "fatefridge-restore-test.ffbackup";

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] RecoveryKey =
        Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();

    private static readonly byte[] WrongRecoveryKey =
        Enumerable.Range(101, 32).Select(index => (byte)index).ToArray();

    private static readonly byte[] DumpBytes =
        Encoding.UTF8.GetBytes("PGDMP fake custom-format dump used by the M13-05 restore tests");

    private static readonly byte[] OriginalImage = Encoding.UTF8.GetBytes("original recipe image");

    private static readonly byte[] RestoredImage = Encoding.UTF8.GetBytes("restored recipe image");

    private static readonly byte[] OriginalDataProtectionKey = Encoding.UTF8.GetBytes("<key id=\"original\"/>");

    private static readonly byte[] RestoredDataProtectionKey = Encoding.UTF8.GetBytes("<key id=\"restored\"/>");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string KeyText => Base64Url.EncodeToString(RecoveryKey);

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------
    // The clean-install path
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_clean_install_restore_with_the_right_key_answers_202_and_restarts()
    {
        // A device that never completed setup has no recovery key file at all.
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = RecordingPgRestoreRunner.Succeeding();
        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(dataDir, runner, signal);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        // No session, no cookie, no CSRF token: before setup the archive plus its
        // key are the credential.
        using var client = factory.CreateClient();

        // The pre-setup gate is still doing its job for every other API route.
        (await client.GetAsync("/api/v1/admin/backups", Ct)).StatusCode
            .Should().Be(HttpStatusCode.Conflict);

        var response = await PostRestoreAsync(client, archivePath, KeyText);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("restore_id").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("filename").GetString().Should().Be(ArchiveFileName);
        payload.TryGetProperty("backup_id", out _).Should()
            .BeFalse("no backup catalogue exists before setup");
        var safetyFilename = payload.GetProperty("safety_backup_filename").GetString()!;
        safetyFilename.Should().NotBeNullOrWhiteSpace();

        await AwaitExecutionAsync(factory);

        // The shared sequence ran: safety archive, marker, graceful stop, files.
        signal.Invocations.Should().Be(1);
        RestoreMarker.Exists(configuration).Should().BeTrue();
        File.Exists(Path.Combine(dataDir, "backups", "safety", safetyFilename)).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(RestoredImage);
        File.ReadAllBytes(Path.Combine(dataDir, "config", "dp-keys", "key-abc.xml"))
            .Should().Equal(RestoredDataProtectionKey);

        // The pasted key became this device's key, so the restored instance can
        // create backups of its own.
        File.ReadAllText(Path.Combine(dataDir, "config", RecoveryKeyStore.FileName))
            .Should().Be(KeyText);

        await using var context = fixture.CreateContext();
        var safety = await context.BackupMetadata
            .AsNoTracking()
            .SingleAsync(backup => backup.Kind == BackupKind.Safety, Ct);

        safety.Status.Should().Be(BackupStatus.Completed);
        safety.Filename.Should().Be(safetyFilename);
        safety.CreatedByUserId.Should().BeNull("no Admin exists before setup");

        // The audit row is the system actor's, in the restored database.
        var audit = await context.AuditEvents
            .AsNoTracking()
            .SingleAsync(entry => entry.EventType == AuditEventTypes.BackupRestored, Ct);

        audit.ActorUserId.Should().BeNull();
        audit.EntityId.Should().Be(ArchiveFileName);
        audit.Reason.Should().Be(RestoreService.SetupRestoreReason);
        audit.IpAddress.Should().Be(TestIp);
        audit.MetadataJson.Should().Contain("\"source\": \"upload\"");
    }

    [Fact]
    public async Task After_setup_completes_the_route_answers_SETUP_ALREADY_COMPLETED()
    {
        await SeedHouseholdAsync();
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(recoveryKey: RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        using var client = factory.CreateClient();
        var response = await PostRestoreAsync(client, archivePath, KeyText);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.SetupAlreadyCompleted);

        // Nothing was even read: the route is dead once setup is complete.
        runner.TerminateCalls.Should().Be(0);
        runner.RestoredPaths.Should().BeEmpty();
        RestoreMarker.Exists(configuration).Should().BeFalse();

        await using var context = fixture.CreateContext();
        (await context.BackupMetadata.AsNoTracking().CountAsync(Ct)).Should().Be(0);
    }

    // ------------------------------------------------------------------
    // Validation failures: no mutation at all
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_malformed_recovery_key_is_a_validation_error_before_anything_is_touched()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var client = factory.CreateClient();

        // Not base64url at all.
        var notBase64 = await PostRestoreAsync(client, archivePath, "%%%not-base64%%%");
        notBase64.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(notBase64)).Should().Be(ErrorCodes.ValidationFailed);
        (await FieldErrorsAsync(notBase64)).Should().ContainKey("recovery_key");

        // Base64url, but not 32 bytes.
        var wrongLength = await PostRestoreAsync(client, archivePath, Base64Url.EncodeToString([1, 2, 3]));
        wrongLength.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsAsync(wrongLength)).Should().ContainKey("recovery_key");

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    [Fact]
    public async Task A_missing_recovery_key_or_archive_is_a_validation_error()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var client = factory.CreateClient();

        var noKey = await PostRestoreAsync(client, archivePath, recoveryKey: null);
        noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsAsync(noKey)).Should().ContainKey("recovery_key");

        var noFile = await PostRestoreAsync(client, archivePath: null, KeyText);
        noFile.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await FieldErrorsAsync(noFile)).Should().ContainKey("file");

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    [Fact]
    public async Task A_wrong_recovery_key_is_refused_before_anything_is_touched()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var client = factory.CreateClient();
        var response = await PostRestoreAsync(client, archivePath, Base64Url.EncodeToString(WrongRecoveryKey));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.BackupKeyMismatch);

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    [Fact]
    public async Task A_tampered_archive_is_refused_before_anything_is_touched()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir);

        // Flip the last byte (the terminator tag): the uploaded checksum is
        // computed from the tampered bytes, so GCM authentication is what fails.
        var bytes = await File.ReadAllBytesAsync(archivePath, Ct);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(archivePath, bytes, Ct);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var client = factory.CreateClient();
        var response = await PostRestoreAsync(client, archivePath, KeyText);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.BackupIntegrityFailed);

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    [Fact]
    public async Task An_archive_from_a_newer_schema_is_refused()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir, "20990101000000_FutureMigration");

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var client = factory.CreateClient();
        var response = await PostRestoreAsync(client, archivePath, KeyText);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.BackupSchemaTooNew);

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    [Fact]
    public async Task An_archive_with_an_unknown_format_is_refused()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = Path.Combine(dataDir, "backups", "not-an-archive.ffbackup");
        await File.WriteAllTextAsync(archivePath, "this is not a Fate & Fridge archive", Ct);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var client = factory.CreateClient();
        var response = await PostRestoreAsync(client, archivePath, KeyText);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.BackupFormatUnsupported);

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    [Fact]
    public async Task The_typed_confirmation_is_required()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var client = factory.CreateClient();
        var response = await PostRestoreAsync(client, archivePath, KeyText, confirmation: "restore");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.ConfirmationMismatch);
        (await FieldErrorsAsync(response)).Should().ContainKey("confirmation");

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    /// <summary>
    /// The real <c>pg_restore</c> path stays skipped on this workstation, exactly
    /// as in the M13-04 suite; the M13-10 image gate verifies it live.
    /// </summary>
    [Fact]
    public async Task Real_pg_restore_is_skipped_when_the_client_tools_are_absent()
    {
        var available = await PgRestoreVersionAvailableAsync();
        if (!available)
        {
            Assert.Skip(
                "pg_restore is not installed on this workstation; the real clean-install restore " +
                "is verified in the runtime image at M13-10.");
            return;
        }

        var versionOutput = await new PgDumpVersionReader().ReadVersionOutputAsync(Ct);
        Assert.Skip(
            "pg_restore is present but the real dump/restore round trip is part of the M13-10 " +
            $"image gate (pg_dump probe: {versionOutput ?? "unavailable"}).");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static async Task<bool> PgRestoreVersionAvailableAsync()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = PgRestoreRunner.DefaultExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--version" },
            });

            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(Ct);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task AwaitExecutionAsync(ApiFactory factory)
    {
        var state = factory.Services.GetRequiredService<RestoreState>();
        await state.Execution.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        state.IsMaintenanceActive.Should().BeFalse();
    }

    private async Task<HttpResponseMessage> PostRestoreAsync(
        HttpClient client,
        string? archivePath,
        string? recoveryKey,
        string confirmation = SetupEndpoints.RestoreConfirmation)
    {
        using var content = new MultipartFormDataContent();

        if (archivePath is not null)
        {
            // The form content owns (and disposes) the stream when the request
            // content is disposed at the end of this method.
            var archive = File.OpenRead(archivePath);
            content.Add(
                new StreamContent(archive),
                "file",
                Path.GetFileName(archivePath));
        }

        if (recoveryKey is not null)
        {
            content.Add(new StringContent(recoveryKey), "recovery_key");
        }

        content.Add(new StringContent(confirmation), "confirmation");

        using var request = new HttpRequestMessage(HttpMethod.Post, SetupRestorePath)
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);

        return await client.SendAsync(request, Ct);
    }

    private ApiFactory CreateFactory(
        string dataDir,
        IPgRestoreRunner runner,
        IRestoreRestartSignal signal) =>
        fixture.CreateFactory(dataDir, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new TestTimeProvider(Now));
            services.RemoveAll<IPgDumpRunner>();
            services.AddSingleton<IPgDumpRunner>(RecordingPgDumpRunner.Writing(DumpBytes));
            services.RemoveAll<IPgRestoreRunner>();
            services.AddSingleton(runner);
            services.RemoveAll<IRestoreRestartSignal>();
            services.AddSingleton(signal);
            services.RemoveAll<IPgDumpVersionReader>();
            services.AddSingleton<IPgDumpVersionReader>(new FakePgDumpVersionReader(PgDump17));
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
        });

    /// <summary>
    /// A data directory in the mounted <c>/data</c> layout: the original recipe
    /// image, the original Data Protection key, and — for a device that has
    /// already completed setup — a recovery key file.
    /// </summary>
    private string SeedDataDirectory(byte[]? recoveryKey)
    {
        var dataDir = fixture.NewDataDirectory();
        var images = Directory.CreateDirectory(Path.Combine(dataDir, "images", "recipes")).FullName;
        var dataProtectionKeys =
            Directory.CreateDirectory(Path.Combine(dataDir, "config", "dp-keys")).FullName;
        Directory.CreateDirectory(Path.Combine(dataDir, "backups"));

        File.WriteAllBytes(Path.Combine(images, "pasta.jpg"), OriginalImage);
        File.WriteAllBytes(Path.Combine(dataProtectionKeys, "key-abc.xml"), OriginalDataProtectionKey);

        if (recoveryKey is not null)
        {
            File.WriteAllText(
                Path.Combine(dataDir, "config", RecoveryKeyStore.FileName),
                Base64Url.EncodeToString(recoveryKey));
        }

        return dataDir;
    }

    /// <summary>
    /// Writes a real encrypted archive containing the dump, one recipe image, and
    /// one Data Protection key, with the given manifest schema version.
    /// </summary>
    private async Task<string> WriteArchiveAsync(string dataDir, string? schemaVersion = null)
    {
        var path = Path.Combine(dataDir, "backups", ArchiveFileName);
        var manifest = new BackupManifest
        {
            AppVersion = AppInfo.Version,
            SchemaVersion = schemaVersion ?? await LatestSchemaVersionAsync(),
            CreatedAt = Now,
        };

        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var writer = await BackupArchiveWriter.WriteAsync(output, RecoveryKey, manifest, Ct);
        try
        {
            await writer.AddEntryAsync(
                RestoreService.DatabaseDumpEntryName,
                new MemoryStream(DumpBytes),
                Ct);
            await writer.AddEntryAsync(
                "images/recipes/pasta.jpg",
                new MemoryStream(RestoredImage),
                Ct);
            await writer.AddEntryAsync(
                "config/dp-keys/key-abc.xml",
                new MemoryStream(RestoredDataProtectionKey),
                Ct);

            await writer.CompleteAsync(Ct);
        }
        finally
        {
            await writer.DisposeAsync();
        }

        return path;
    }

    private async Task<string> LatestSchemaVersionAsync()
    {
        await using var context = fixture.CreateContext();
        return context.Database.GetMigrations().Last();
    }

    private async Task AssertNothingTouchedAsync(
        ApiFactory factory,
        string dataDir,
        RecordingPgRestoreRunner runner)
    {
        var state = factory.Services.GetRequiredService<RestoreState>();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        state.IsMaintenanceActive.Should().BeFalse();
        RestoreMarker.Exists(configuration).Should().BeFalse();
        runner.TerminateCalls.Should().Be(0);
        runner.RestoredPaths.Should().BeEmpty();

        File.Exists(Path.Combine(dataDir, "config", RecoveryKeyStore.FileName)).Should()
            .BeFalse("the pasted key is adopted only after the archive validates");

        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(OriginalImage);

        await using var context = fixture.CreateContext();
        (await context.BackupMetadata.AsNoTracking().CountAsync(Ct)).Should()
            .Be(0, "no safety backup is created before validation passes");
        (await context.AuditEvents.AsNoTracking()
                .CountAsync(entry => entry.EventType == AuditEventTypes.BackupRestored, Ct))
            .Should().Be(0);
    }

    private async Task SeedHouseholdAsync()
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M13 Setup Restore Household",
            Timezone = "America/Chicago",
            DefaultDinerCount = 4,
            WeekStartsOn = 0,
            StartupPage = StartupPage.Calendar,
            AuthMode = AuthMode.Multiuser,
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

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return payload.RootElement.Clone();
    }

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response) =>
        (await ReadJsonAsync(response)).GetProperty("error").GetProperty("code").GetString()!;

    private static async Task<Dictionary<string, JsonElement>> FieldErrorsAsync(
        HttpResponseMessage response)
    {
        var payload = await ReadJsonAsync(response);
        return payload.GetProperty("error").GetProperty("details").GetProperty("field_errors")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
    }
}
