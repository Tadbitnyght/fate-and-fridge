using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FateAndFridge.Api.Auth;
using FateAndFridge.Api.Backup;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using FateAndFridge.Api.Middleware;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M13-03 backup service and admin API (docs/M13_PLAN.md §1.4–§1.8, §2.1, §2.2,
/// §4; docs/API.md §18). Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// <para>
/// <c>pg_dump</c> is not installed on the development workstation, so the
/// orchestration runs against a fake <see cref="IPgDumpRunner"/>; the only real
/// binary test is
/// <see cref="Real_pg_dump_produces_a_readable_archive_or_is_skipped"/>, which
/// skips with a clear message when the client is absent or older than the
/// server (verified in the runtime image at M13-10).
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(M13BackupCollection.Name)]
public sealed class M13BackupServiceIntegrationTests(M13BackupFixture fixture) : IAsyncLifetime
{
    private const string BackupsPath = "/api/v1/admin/backups";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m13-backup-service-test";
    private const string TestIp = "203.0.113.21";
    private const string PgDump17 = "pg_dump (PostgreSQL) 17.4 (Debian 17.4-1.pgdg120+1)";
    private const string PgDump16 = "pg_dump (PostgreSQL) 16.4 (Debian 16.4-1.pgdg120+1)";

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] RecoveryKey = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();

    private static readonly byte[] DumpBytes = Encoding.UTF8.GetBytes(
        "PGDMP fake custom-format dump used by the M13-03 service tests");

    private static readonly byte[] ImageBytes = RandomNumberGenerator.GetBytes(2048);

    private static readonly byte[] DataProtectionKeyBytes =
        """<key id="m13-test-key">not-a-real-key-ring-entry</key>"""u8.ToArray();

    /// <summary>
    /// A Data Protection provider file name inside <c>config/dp-keys</c>
    /// (<c>key-&lt;guid&gt;.xml</c>). The hosted provider may add these during a
    /// test; the backup must archive them as legitimate config, and nothing else
    /// beyond the seeded content may appear.
    /// </summary>
    private static readonly Regex GeneratedDataProtectionKeyName = new(
        @"^config/dp-keys/key-[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\.xml$",
        RegexOptions.CultureInvariant);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Create_writes_a_completed_row_and_an_archive_that_reads_back_with_the_recovery_key()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        using var factory = CreateFactory(dataDir, RecordingPgDumpRunner.Writing(DumpBytes));
        var clock = new TestTimeProvider(Now);

        var outcome = await CreateAsync(factory, "Before the upgrade", actorUserId: null);

        var backup = outcome.Should().BeOfType<BackupCreateOutcome.Success>().Subject.Backup;
        backup.Status.Should().Be(BackupStatus.Completed);
        backup.Kind.Should().Be(BackupKind.Manual);
        backup.AppVersion.Should().Be(AppInfo.Version);
        backup.SchemaVersion.Should().EndWith("M13Backups");
        backup.CreatedAt.Should().Be(Now);
        backup.Error.Should().BeNull();

        var expectedFilename = $"fatefridge-{AppInfo.Version}-20261021T120000Z.ffbackup";
        backup.Filename.Should().Be(expectedFilename);

        var archivesDirectory = Path.Combine(dataDir, "backups");
        var archivePath = Path.Combine(archivesDirectory, backup.Filename);
        var bytes = await File.ReadAllBytesAsync(archivePath, Ct);
        backup.Checksum.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        backup.SizeBytes.Should().Be(bytes.Length);
        backup.Checksum.Should().HaveLength(64);

        // Nothing is left behind: no partial archive and no dump temp file.
        Directory.EnumerateFiles(archivesDirectory, "*.tmp").Should().BeEmpty();
        Directory.EnumerateFiles(Path.Combine(archivesDirectory, ".tmp")).Should().BeEmpty();

        var (entries, manifest) = await ReadArchiveAsync(archivePath);
        var names = entries.Select(entry => entry.Name).ToArray();

        // The hosted Data Protection key ring lives in config/dp-keys and may
        // have generated its own key-<guid>.xml next to the seeded key. Those
        // generated keys are legitimate archive content; every other entry must
        // be exactly the approved content set (docs/M13_PLAN.md §1.5).
        string[] required =
        [
            "database.dump",
            "images/recipes/pasta.jpg",
            "config/dp-keys/key-abc.xml",
        ];

        names.Should().Contain(required);
        names.Should().OnlyContain(name =>
            required.Contains(name, StringComparer.Ordinal)
            || name == BackupArchiveFormat.ManifestEntryName
            || GeneratedDataProtectionKeyName.IsMatch(name));
        names.Should().NotContain(
            name => name.Contains("recovery.key", StringComparison.Ordinal),
            "the recovery key must never be inside an archive");
        names[^1].Should().Be("manifest.json");

        entries.Single(entry => entry.Name == "database.dump").Data.Should().Equal(DumpBytes);
        entries.Single(entry => entry.Name == "images/recipes/pasta.jpg").Data.Should().Equal(ImageBytes);
        entries.Single(entry => entry.Name == "config/dp-keys/key-abc.xml").Data
            .Should().Equal(DataProtectionKeyBytes);

        manifest.AppVersion.Should().Be(AppInfo.Version);
        manifest.SchemaVersion.Should().Be(backup.SchemaVersion);
        manifest.CreatedAt.Should().Be(Now);
        manifest.EntryCount.Should()
            .Be(names.Length - 1, "the manifest describes content entries and excludes itself");
        manifest.Entries.Select(entry => entry.Path).Should().Equal(names[..^1]);
        foreach (var entry in manifest.Entries)
        {
            var content = entries.Single(candidate => candidate.Name == entry.Path).Data;
            entry.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(content)));
            entry.SizeBytes.Should().Be(content.Length);
        }

        var audits = await ReadAuditRowsAsync();
        var created = audits.Should().ContainSingle(row => row.EventType == AuditEventTypes.BackupCreated).Subject;
        created.EntityType.Should().Be(AuditEntityTypes.Backup);
        created.EntityId.Should().Be(backup.Id.ToString(CultureInfo.InvariantCulture));
        created.Reason.Should().Be("Before the upgrade");

        clock.UtcNow.Should().Be(Now);
    }

    [Fact]
    public async Task Create_is_refused_without_a_row_or_an_archive_when_pg_dump_is_too_old()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        using var factory = CreateFactory(
            dataDir,
            RecordingPgDumpRunner.Writing(DumpBytes),
            clientVersionOutput: PgDump16);

        var outcome = await CreateAsync(factory, "Before the upgrade", actorUserId: null);

        var failure = outcome.Should().BeOfType<BackupCreateOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupClientTooOld);
        failure.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        await using (var context = fixture.CreateContext())
        {
            (await context.BackupMetadata.AsNoTracking().CountAsync(Ct)).Should().Be(0);
        }

        var backupsDirectory = Path.Combine(dataDir, "backups");
        if (Directory.Exists(backupsDirectory))
        {
            Directory.EnumerateFileSystemEntries(backupsDirectory).Should().BeEmpty(
                "a refused backup must not create any archive");
        }
    }

    [Fact]
    public async Task Create_is_refused_with_a_key_missing_error_when_the_recovery_key_file_is_absent()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: false);
        var runner = RecordingPgDumpRunner.Writing(DumpBytes);
        using var factory = CreateFactory(dataDir, runner);

        var outcome = await CreateAsync(factory, "Before the upgrade", actorUserId: null);

        var failure = outcome.Should().BeOfType<BackupCreateOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupKeyMissing);
        failure.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        runner.OutputPaths.Should().BeEmpty("no dump may start without a usable key");
        await using (var context = fixture.CreateContext())
        {
            (await context.BackupMetadata.AsNoTracking().CountAsync(Ct)).Should().Be(0);
        }
    }

    [Fact]
    public async Task A_runner_failure_marks_the_row_failed_removes_partial_files_and_writes_no_success_audit()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        using var factory = CreateFactory(
            dataDir,
            RecordingPgDumpRunner.FailingAfterPartialWrite("pg_dump exited with code 1"));

        var outcome = await CreateAsync(factory, "Before the upgrade", actorUserId: null);

        var failure = outcome.Should().BeOfType<BackupCreateOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupFailed);
        failure.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        BackupMetadata row;
        await using (var context = fixture.CreateContext())
        {
            row = await context.BackupMetadata.AsNoTracking().SingleAsync(Ct);
        }

        row.Status.Should().Be(BackupStatus.Failed);
        row.Error.Should().Contain("pg_dump exited with code 1");
        row.Checksum.Should().BeEmpty();
        row.SizeBytes.Should().Be(0);

        var archivesDirectory = Path.Combine(dataDir, "backups");
        Directory.EnumerateFiles(archivesDirectory, "*.ffbackup", SearchOption.AllDirectories)
            .Should().BeEmpty("a failed backup must not present a partial archive");
        Directory.EnumerateFiles(archivesDirectory, "*.tmp", SearchOption.AllDirectories)
            .Should().BeEmpty("the dump and archive temp files must be removed");
        Directory.EnumerateFiles(Path.Combine(archivesDirectory, ".tmp"), "*", SearchOption.AllDirectories)
            .Should().BeEmpty();

        (await ReadAuditRowsAsync())
            .Should().NotContain(row => row.EventType == AuditEventTypes.BackupCreated);
    }

    [Fact]
    public async Task A_second_concurrent_create_is_refused_and_only_one_archive_is_written()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        var runner = new GatedPgDumpRunner();
        using var factory = CreateFactory(dataDir, runner);

        var first = CreateAsync(factory, "Scheduled maintenance", actorUserId: null);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var second = await CreateAsync(factory, "Second attempt", actorUserId: null);
        second.Should().BeOfType<BackupCreateOutcome.Failure>()
            .Which.Code.Should().Be(ErrorCodes.BackupInProgress);

        runner.Release.TrySetResult();
        var firstOutcome = await first;
        firstOutcome.Should().BeOfType<BackupCreateOutcome.Success>();

        runner.Invocations.Should().Be(1, "the gate must serialize dumps in this process");
        Directory.EnumerateFiles(Path.Combine(dataDir, "backups"), "*.ffbackup")
            .Should().ContainSingle();

        await using var context = fixture.CreateContext();
        (await context.BackupMetadata.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task List_download_and_delete_work_end_to_end_with_the_typed_confirmation_and_audit_rows()
    {
        await fixture.ResetAsync(Ct);
        await SeedHouseholdAsync(AuthMode.Multiuser);
        var admin = await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        var clock = new TestTimeProvider(Now);
        using var factory = CreateFactory(dataDir, RecordingPgDumpRunner.Writing(DumpBytes), clock: clock);
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var firstResponse = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            BackupsPath,
            """{"reason":"First manual backup"}""",
            session.CsrfToken);
        firstResponse.StatusCode.Should()
            .Be(HttpStatusCode.Created, "a synchronous create returns the completed row");
        var first = await ReadJsonAsync(firstResponse);
        first.GetProperty("status").GetString().Should().Be("completed");
        first.GetProperty("kind").GetString().Should().Be("manual");
        first.GetProperty("checksum").GetString().Should().HaveLength(64);
        var firstId = first.GetProperty("id").GetInt64();
        var firstFilename = first.GetProperty("filename").GetString()!;

        clock.Advance(TimeSpan.FromSeconds(1));
        var secondResponse = await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            BackupsPath,
            """{"reason":"Second manual backup"}""",
            session.CsrfToken);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await ReadJsonAsync(secondResponse);
        second.GetProperty("status").GetString().Should().Be("completed");
        var secondId = second.GetProperty("id").GetInt64();
        second.GetProperty("filename").GetString().Should()
            .Be($"fatefridge-{AppInfo.Version}-20261021T120001Z.ffbackup");

        var list = await ReadJsonAsync(await GetAsync(session.Client, BackupsPath));
        list.ValueKind.Should().Be(JsonValueKind.Array);
        list.GetArrayLength().Should().Be(2);
        list[0].GetProperty("id").GetInt64().Should().Be(secondId, "the list is newest first");
        list[1].GetProperty("id").GetInt64().Should().Be(firstId);
        foreach (var field in new[]
                 {
                     "id", "filename", "kind", "status", "app_version", "schema_version",
                     "created_at", "size_bytes", "checksum", "error",
                 })
        {
            list[0].TryGetProperty(field, out _).Should().BeTrue($"the list row must expose '{field}'");
        }

        var download = await GetAsync(session.Client, $"{BackupsPath}/{firstId}/download");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
        download.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        download.Content.Headers.ContentDisposition.FileName.Should().Be(firstFilename);
        var downloaded = await download.Content.ReadAsByteArrayAsync(Ct);
        var archived = await File.ReadAllBytesAsync(
            Path.Combine(dataDir, "backups", firstFilename), Ct);
        downloaded.Should().Equal(archived);

        var wrongConfirmation = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{BackupsPath}/{firstId}",
            """{"reason":"No longer needed","confirmation":"delete"}""",
            session.CsrfToken);
        wrongConfirmation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(wrongConfirmation)).Should().Be(ErrorCodes.ConfirmationMismatch);
        File.Exists(Path.Combine(dataDir, "backups", firstFilename)).Should().BeTrue();

        var deleted = await SendJsonAsync(
            session.Client,
            HttpMethod.Delete,
            $"{BackupsPath}/{firstId}",
            """{"reason":"No longer needed","confirmation":"DELETE"}""",
            session.CsrfToken);
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(deleted)).GetProperty("status").GetString().Should().Be("deleted");
        File.Exists(Path.Combine(dataDir, "backups", firstFilename)).Should().BeFalse();

        var downloadAfterDelete = await GetAsync(session.Client, $"{BackupsPath}/{firstId}/download");
        downloadAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(downloadAfterDelete)).Should().Be(ErrorCodes.BackupNotFound);

        var audits = await ReadAuditRowsAsync();
        audits.Count(row => row.EventType == AuditEventTypes.BackupCreated).Should().Be(2);
        var deleteAudit = audits
            .Should().ContainSingle(row => row.EventType == AuditEventTypes.BackupDeleted).Subject;
        deleteAudit.EntityType.Should().Be(AuditEntityTypes.Backup);
        deleteAudit.EntityId.Should().Be(firstId.ToString(CultureInfo.InvariantCulture));
        deleteAudit.ActorUserId.Should().Be(admin.Id);
        deleteAudit.Reason.Should().Be("No longer needed");
    }

    [Fact]
    public async Task Download_reports_a_missing_file_when_the_row_exists_but_the_archive_is_gone()
    {
        await fixture.ResetAsync(Ct);
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        using var factory = CreateFactory(dataDir, RecordingPgDumpRunner.Writing(DumpBytes));
        using var session = await SignInAsync(factory, "root", AdminPassword);

        var created = await ReadJsonAsync(await SendJsonAsync(
            session.Client,
            HttpMethod.Post,
            BackupsPath,
            """{"reason":"Will lose its file"}""",
            session.CsrfToken));
        var id = created.GetProperty("id").GetInt64();
        var filename = created.GetProperty("filename").GetString()!;

        File.Delete(Path.Combine(dataDir, "backups", filename));

        var response = await GetAsync(session.Client, $"{BackupsPath}/{id}/download");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(response)).Should().Be(ErrorCodes.BackupFileMissing);
    }

    [Fact]
    public async Task Prune_keeps_the_newest_five_safety_archives_and_never_touches_manual_ones()
    {
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        var safetyDirectory = Directory.CreateDirectory(Path.Combine(dataDir, "backups", "safety")).FullName;
        var archivesDirectory = Directory.CreateDirectory(Path.Combine(dataDir, "backups")).FullName;

        var safetyRows = new List<BackupMetadata>();
        for (var index = 0; index < 7; index++)
        {
            var filename = $"safety-{index}.ffbackup";
            await File.WriteAllTextAsync(Path.Combine(safetyDirectory, filename), $"safety {index}", Ct);
            safetyRows.Add(await SeedBackupAsync(filename, BackupKind.Safety, BackupStatus.Completed, Now.AddDays(-index)));
        }

        var manualRows = new List<BackupMetadata>();
        for (var index = 0; index < 2; index++)
        {
            var filename = $"manual-{index}.ffbackup";
            await File.WriteAllTextAsync(Path.Combine(archivesDirectory, filename), $"manual {index}", Ct);
            manualRows.Add(await SeedBackupAsync(
                filename, BackupKind.Manual, BackupStatus.Completed, Now.AddDays(-(100 + index))));
        }

        using var factory = CreateFactory(dataDir, RecordingPgDumpRunner.Writing(DumpBytes));
        var deletedCount = await WithBackupServiceAsync(
            factory.Services,
            service => service.PruneSafetyBackupsAsync(5, Ct));

        deletedCount.Should().Be(2);
        await using var context = fixture.CreateContext();
        var rows = await context.BackupMetadata.AsNoTracking().ToListAsync(Ct);

        foreach (var row in rows.Where(row => row.Kind == BackupKind.Safety))
        {
            var kept = safetyRows.Take(5).Any(candidate => candidate.Id == row.Id);
            row.Status.Should().Be(kept ? BackupStatus.Completed : BackupStatus.Deleted);
            File.Exists(Path.Combine(safetyDirectory, row.Filename)).Should().Be(kept);
        }

        foreach (var manual in manualRows)
        {
            rows.Single(row => row.Id == manual.Id).Status.Should()
                .Be(BackupStatus.Completed, "manual backups are never auto-pruned");
            File.Exists(Path.Combine(archivesDirectory, manual.Filename)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Every_route_is_admin_only()
    {
        await fixture.ResetAsync(Ct);
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        using var factory = CreateFactory(dataDir, RecordingPgDumpRunner.Writing(DumpBytes));

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync(BackupsPath, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await SendJsonAsync(
            anonymous, HttpMethod.Post, BackupsPath, """{"reason":"anonymous"}""", csrfToken: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);

        using var member = await SignInAsync(factory, "member", MemberPassword);
        (await GetAsync(member.Client, BackupsPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await SignInAsync(factory, "root", AdminPassword);
        (await GetAsync(admin.Client, BackupsPath)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_single_user_system_actor_can_list_backups()
    {
        await fixture.ResetAsync(Ct);
        await SeedHouseholdAsync(AuthMode.SingleUser);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        using var factory = CreateFactory(dataDir, RecordingPgDumpRunner.Writing(DumpBytes));
        using var client = factory.CreateClient();

        var response = await GetAsync(client, BackupsPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).ValueKind.Should().Be(JsonValueKind.Array);
    }

    /// <summary>
    /// The only test that uses the real binary. It is skipped (with the reason
    /// visible in the run) when pg_dump is absent or older than the container's
    /// server; the real path is verified in the runtime image at M13-10.
    /// </summary>
    [Fact]
    public async Task Real_pg_dump_produces_a_readable_archive_or_is_skipped()
    {
        var versionOutput = await new PgDumpVersionReader().ReadVersionOutputAsync(Ct);
        if (versionOutput is null)
        {
            Assert.Skip(
                "pg_dump is not installed on this workstation; the real dump path is " +
                "verified in the runtime image at M13-10.");
            return;
        }

        if (!PgVersionParser.TryParseClientMajor(versionOutput, out var clientMajor))
        {
            Assert.Skip($"The installed pg_dump version could not be parsed: '{versionOutput}'.");
            return;
        }

        await fixture.ResetAsync(Ct);
        var dataDir = SeedDataDirectory(withRecoveryKey: true);
        using var factory = new ApiFactory(
            fixture.ConnectionString,
            migrateOnStartup: false,
            dataDir,
            services =>
            {
                // Only the version probe is faked; the runner stays the real
                // PgDumpRunner and therefore starts the real process.
                services.RemoveAll<IPgDumpVersionReader>();
                services.AddSingleton<IPgDumpVersionReader>(new FakePgDumpVersionReader(versionOutput));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new TestTimeProvider(Now));
                services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
            });

        using var scope = factory.Services.CreateScope();
        var tooling = await scope.ServiceProvider.GetRequiredService<PgToolingInfo>().ProbeAsync(Ct);
        if (!tooling.SupportsBackup)
        {
            Assert.Skip(
                $"pg_dump major {clientMajor} is older than server major {tooling.ServerMajor}; " +
                "the runtime image pins a compatible client (M13-01/M13-10).");
            return;
        }

        var outcome = await scope.ServiceProvider.GetRequiredService<BackupService>()
            .CreateAsync("Real pg_dump smoke test", actorUserId: null, ipAddress: null, Ct);

        var backup = outcome.Should().BeOfType<BackupCreateOutcome.Success>().Subject.Backup;
        backup.Status.Should().Be(BackupStatus.Completed);

        var archivePath = Path.Combine(dataDir, "backups", backup.Filename);
        File.Exists(archivePath).Should().BeTrue();
        backup.SizeBytes.Should().Be(new FileInfo(archivePath).Length);

        var (entries, _) = await ReadArchiveAsync(archivePath);
        var dump = entries.Single(entry => entry.Name == "database.dump");
        dump.Data.Take(5).Should().Equal("PGDMP"u8.ToArray(), "a custom-format pg_dump starts with PGDMP");
    }

    private async Task<BackupCreateOutcome> CreateAsync(
        ApiFactory factory,
        string reason,
        long? actorUserId) =>
        await WithBackupServiceAsync(
            factory.Services,
            service => service.CreateAsync(reason, actorUserId, TestIp, Ct));

    private static async Task<T> WithBackupServiceAsync<T>(
        IServiceProvider provider,
        Func<BackupService, Task<T>> action)
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<BackupService>());
    }

    private ApiFactory CreateFactory(
        string dataDir,
        IPgDumpRunner runner,
        string clientVersionOutput = PgDump17,
        TestTimeProvider? clock = null) =>
        fixture.CreateFactory(dataDir, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock ?? new TestTimeProvider(Now));
            services.RemoveAll<IPgDumpRunner>();
            services.AddSingleton(runner);
            services.RemoveAll<IPgDumpVersionReader>();
            services.AddSingleton<IPgDumpVersionReader>(new FakePgDumpVersionReader(clientVersionOutput));
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();
        });

    /// <summary>
    /// Creates a data directory containing the recovery key, one recipe image,
    /// and one Data Protection key, mirroring the mounted <c>/data</c> layout.
    /// </summary>
    private string SeedDataDirectory(bool withRecoveryKey)
    {
        var dataDir = fixture.NewDataDirectory();
        var images = Directory.CreateDirectory(Path.Combine(dataDir, "images", "recipes")).FullName;
        var dpKeys = Directory.CreateDirectory(Path.Combine(dataDir, "config", "dp-keys")).FullName;

        File.WriteAllBytes(Path.Combine(images, "pasta.jpg"), ImageBytes);
        File.WriteAllBytes(Path.Combine(dpKeys, "key-abc.xml"), DataProtectionKeyBytes);

        if (withRecoveryKey)
        {
            File.WriteAllText(
                Path.Combine(dataDir, "config", RecoveryKeyStore.FileName),
                Base64Url.EncodeToString(RecoveryKey));
        }

        return dataDir;
    }

    private static async Task<(List<(string Name, byte[] Data)> Entries, BackupManifest Manifest)>
        ReadArchiveAsync(string path)
    {
        await using var input = File.OpenRead(path);
        await using var reader = await BackupArchiveReader.OpenAsync(
            input, RecoveryKey, expectedSha256: null, Ct);

        var entries = new List<(string Name, byte[] Data)>();
        await foreach (var entry in reader.ReadEntriesAsync(Ct))
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            using var buffer = new MemoryStream();
            await entry.Content.CopyToAsync(buffer, Ct);
            entries.Add((entry.Name, buffer.ToArray()));
        }

        reader.IsVerified.Should().BeTrue();
        reader.Manifest.Should().NotBeNull();
        return (entries, reader.Manifest!);
    }

    private async Task<BackupMetadata> SeedBackupAsync(
        string filename,
        BackupKind kind,
        BackupStatus status,
        DateTimeOffset createdAt)
    {
        await using var context = fixture.CreateContext();
        var row = new BackupMetadata
        {
            Filename = filename,
            AppVersion = AppInfo.Version,
            SchemaVersion = "20261021023131_M13Backups",
            CreatedAt = createdAt,
            Checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(filename))),
            SizeBytes = 12,
            Kind = kind,
            Status = status,
        };

        context.BackupMetadata.Add(row);
        await context.SaveChangesAsync(Ct);
        return row;
    }

    private async Task<List<AuditEvent>> ReadAuditRowsAsync()
    {
        await using var context = fixture.CreateContext();
        return await context.AuditEvents.AsNoTracking().OrderBy(row => row.Id).ToListAsync(Ct);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M13 Backup Household",
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

    private static async Task<SignedInClient> SignInAsync(
        WebApplicationFactory<Program> factory,
        string username,
        string password)
    {
        var cookies = new CookieContainerHandler(new System.Net.CookieContainer());
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

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);
        return await client.SendAsync(request, Ct);
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
