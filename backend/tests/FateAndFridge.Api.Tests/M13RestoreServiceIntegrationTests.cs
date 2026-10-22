using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// M13-04 restore backend (docs/M13_PLAN.md §1.9, §2.2, §4; docs/SECURITY.md
/// §12; docs/API.md §18). Requires a Docker daemon (Testcontainers PostgreSQL).
/// <para>
/// No PostgreSQL client tools exist on the development workstation, so the
/// orchestration runs against a fake <see cref="IPgRestoreRunner"/> and
/// <c>pg_restore</c> is only exercised for real in the runtime image at M13-10;
/// the conditional real-binary test skips with a visible reason.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(M13BackupCollection.Name)]
public sealed class M13RestoreServiceIntegrationTests(M13BackupFixture fixture) : IAsyncLifetime
{
    private const string BackupsPath = "/api/v1/admin/backups";
    private const string RestorePath = "/api/v1/admin/backups/restore";
    private const string AdminPassword = "root horse battery staple";
    private const string MemberPassword = "correct horse battery staple";
    private const string TestUserAgent = "m13-restore-service-test";
    private const string TestIp = "203.0.113.31";
    private const string PgDump17 = "pg_dump (PostgreSQL) 17.4 (Debian 17.4-1.pgdg120+1)";
    private const string Reason = "Restore the pre-upgrade archive";

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset SeedTimestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly byte[] RecoveryKey =
        Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();

    private static readonly byte[] WrongRecoveryKey =
        Enumerable.Range(101, 32).Select(index => (byte)index).ToArray();

    private static readonly byte[] DumpBytes =
        Encoding.UTF8.GetBytes("PGDMP fake custom-format dump used by the M13-04 restore tests");

    private static readonly byte[] OriginalImage = Encoding.UTF8.GetBytes("original recipe image");

    private static readonly byte[] RestoredImage = Encoding.UTF8.GetBytes("restored recipe image");

    private static readonly byte[] OriginalDataProtectionKey = Encoding.UTF8.GetBytes("<key id=\"original\"/>");

    private static readonly byte[] RestoredDataProtectionKey = Encoding.UTF8.GetBytes("<key id=\"restored\"/>");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => new(fixture.ResetAsync(Ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------------
    // Validation failures: no mutation, no maintenance mode, no safety backup
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_wrong_recovery_key_is_refused_before_anything_is_touched()
    {
        var dataDir = SeedDataDirectory(WrongRecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = RecordingPgRestoreRunner.Succeeding();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        var outcome = await BeginAsync(factory, UploadSource(archivePath), "restore-wrong-key");

        var failure = outcome.Should().BeOfType<RestoreStartOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupKeyMismatch);
        failure.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        await AssertNothingTouchedAsync(factory, dataDir, runner);
    }

    [Fact]
    public async Task A_tampered_archive_is_refused_before_anything_is_touched()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);

        // Flip the last byte (the terminator tag): the whole-file checksum is
        // computed after tampering so the GCM terminator authentication is what
        // fails, not the cheap checksum.
        var bytes = await File.ReadAllBytesAsync(archivePath, Ct);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(archivePath, bytes, Ct);
        var tamperedChecksum = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            new RecordingRestartSignal());

        var outcome = await BeginAsync(
            factory,
            new RestoreSource.Upload(archivePath, "tampered.ffbackup", tamperedChecksum),
            "restore-tampered");

        var failure = outcome.Should().BeOfType<RestoreStartOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupIntegrityFailed);
        failure.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task A_checksum_that_does_not_match_the_stored_row_is_refused()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);
        var row = await SeedBackupRowAsync(dataDir, archivePath, checksumOverride: new string('a', 64));

        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            new RecordingRestartSignal());

        var outcome = await BeginAsync(factory, new RestoreSource.StoredBackup(row.Id), "restore-checksum");

        var failure = outcome.Should().BeOfType<RestoreStartOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupIntegrityFailed);
    }

    [Fact]
    public async Task An_archive_from_a_newer_schema_is_refused()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir, "20990101000000_FutureMigration");

        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            new RecordingRestartSignal());

        var outcome = await BeginAsync(factory, UploadSource(archivePath), "restore-too-new");

        var failure = outcome.Should().BeOfType<RestoreStartOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupSchemaTooNew);
        failure.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task An_archive_with_an_unknown_schema_version_is_refused()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir, "19990101000000_AncientMigration");

        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            new RecordingRestartSignal());

        var outcome = await BeginAsync(factory, UploadSource(archivePath), "restore-unknown-schema");

        var failure = outcome.Should().BeOfType<RestoreStartOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupFormatUnsupported);
    }

    [Fact]
    public async Task A_missing_recovery_key_is_refused()
    {
        var dataDir = SeedDataDirectory(recoveryKey: null);
        var archivePath = await WriteArchiveAsync(dataDir);

        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            new RecordingRestartSignal());

        var outcome = await BeginAsync(factory, UploadSource(archivePath), "restore-no-key");

        var failure = outcome.Should().BeOfType<RestoreStartOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupKeyMissing);
        failure.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task A_missing_backup_row_is_not_found()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);

        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            new RecordingRestartSignal());

        var outcome = await BeginAsync(factory, new RestoreSource.StoredBackup(987654), "restore-missing");

        var failure = outcome.Should().BeOfType<RestoreStartOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.BackupNotFound);
        failure.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------
    // The successful path
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_successful_restore_runs_the_documented_sequence()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);
        var row = await SeedBackupRowAsync(dataDir, archivePath);
        await SeedUserAndSessionAsync();

        var runner = RecordingPgRestoreRunner.Succeeding();
        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(dataDir, runner, signal);
        var state = factory.Services.GetRequiredService<RestoreState>();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        var maintenanceObservedDuringRestore = false;
        runner.OnRestore = _ => maintenanceObservedDuringRestore = state.IsMaintenanceActive;

        var outcome = await BeginAsync(factory, new RestoreSource.StoredBackup(row.Id), "restore-success");
        var accepted = outcome.Should().BeOfType<RestoreStartOutcome.Accepted>().Subject;
        accepted.Operation.RestoreId.Should().Be("restore-success");
        accepted.Operation.BackupId.Should().Be(row.Id);

        // Step 4: the safety backup exists, completed, with a checksum and a file.
        await using (var context = fixture.CreateContext())
        {
            var safety = await context.BackupMetadata
                .AsNoTracking()
                .SingleAsync(backup => backup.Kind == BackupKind.Safety, Ct);

            safety.Status.Should().Be(BackupStatus.Completed);
            safety.Filename.Should().Be(accepted.Operation.SafetyBackupFilename);
            safety.Checksum.Should().HaveLength(64);
            safety.Error.Should().BeNull();
            File.Exists(Path.Combine(dataDir, "backups", "safety", safety.Filename))
                .Should().BeTrue("the safety archive is the recovery point");
        }

        state.IsMaintenanceActive.Should().BeFalse("validation and the safety backup do not mutate");

        var execution = await Restores(factory).ExecuteAsync(accepted.Operation, Ct);

        execution.Should().BeOfType<RestoreExecutionOutcome.Success>();

        // Step 7: connections terminated, then one single-transaction pg_restore.
        runner.TerminateCalls.Should().Be(1);
        runner.RestoredPaths.Should().ContainSingle();
        runner.RestoredPaths[0].Should().Be(Path.Combine(
            RestoreService.RestoreDirectory(configuration, "restore-success"),
            "staging",
            RestoreService.DatabaseDumpEntryName));

        maintenanceObservedDuringRestore.Should().BeTrue(
            "maintenance mode is on while pg_restore runs");

        // Step 8: no session survives.
        await using (var context = fixture.CreateContext())
        {
            (await context.UserSessions.AsNoTracking().CountAsync(Ct)).Should().Be(0);
        }

        // Step 9: the audit row exists in the restored database.
        await using (var context = fixture.CreateContext())
        {
            var audit = await context.AuditEvents
                .AsNoTracking()
                .SingleAsync(entry => entry.EventType == AuditEventTypes.BackupRestored, Ct);

            audit.EntityType.Should().Be(AuditEntityTypes.Backup);
            audit.EntityId.Should().Be(row.Filename);
            audit.Reason.Should().Be(Reason);
            audit.IpAddress.Should().Be(TestIp);
            audit.MetadataJson.Should().Contain("restore-success");
        }

        // Step 10: images/ and config/dp-keys/ were replaced.
        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(RestoredImage);
        File.ReadAllBytes(Path.Combine(dataDir, "config", "dp-keys", "key-abc.xml"))
            .Should().Equal(RestoredDataProtectionKey);

        // Step 11: marker plus graceful stop.
        RestoreMarker.Exists(configuration).Should().BeTrue();
        var marker = RestoreMarker.TryRead(configuration, NullLogger());
        marker.Should().NotBeNull();
        marker!.RestoreId.Should().Be("restore-success");
        marker.BackupFilename.Should().Be(row.Filename);
        marker.SafetyBackupFilename.Should().Be(accepted.Operation.SafetyBackupFilename);
        marker.SchemaVersion.Should().Be(await LatestSchemaVersionAsync());

        signal.Invocations.Should().Be(1);

        Directory.Exists(RestoreService.RestoreDirectory(configuration, "restore-success")).Should().BeFalse(
            "a successful restore reclaims its upload, staging, and swapped-out originals");

        state.IsMaintenanceActive.Should().BeFalse();
        state.Phase.Should().Be("completed");
        state.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_terminated_connection_is_retried_and_the_restore_audit_row_is_still_written()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);
        await SeedUserAndSessionAsync();

        var recorder = new RecordingLoggerProvider();
        var runner = new TerminatingPgRestoreRunner(fixture.ConnectionString);
        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(dataDir, runner, signal, recorder);
        var state = factory.Services.GetRequiredService<RestoreState>();

        var outcome = await BeginAsync(factory, UploadSource(archivePath), "restore-terminated");
        var accepted = outcome.Should().BeOfType<RestoreStartOutcome.Accepted>().Subject;

        var execution = await Restores(factory).ExecuteAsync(accepted.Operation, Ct);

        execution.Should().BeOfType<RestoreExecutionOutcome.Success>(
            "the post-commit step must recover from the connection the runner terminated");
        state.Phase.Should().Be("completed");
        state.LastError.Should().BeNull();

        recorder.Messages.Should().Contain(
            message => message.Contains("post_step_error", StringComparison.Ordinal)
                && message.Contains("retrying once", StringComparison.Ordinal),
            "the first revoke attempt hits the pooled connection killed by the runner");

        await using var context = fixture.CreateContext();

        var audit = await context.AuditEvents
            .AsNoTracking()
            .SingleAsync(entry => entry.EventType == AuditEventTypes.BackupRestored, Ct);
        audit.EntityType.Should().Be(AuditEntityTypes.Backup);
        audit.EntityId.Should().Be(Path.GetFileName(archivePath));
        audit.Reason.Should().Be(Reason);
        audit.MetadataJson.Should().Contain("restore-terminated");

        (await context.UserSessions.AsNoTracking().CountAsync(Ct)).Should().Be(0);
        signal.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task Two_restores_are_refused_and_so_is_a_backup_during_a_restore()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);

        var runner = new GatedPgRestoreRunner();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        var first = await BeginAsync(factory, UploadSource(archivePath), "restore-first");
        var accepted = first.Should().BeOfType<RestoreStartOutcome.Accepted>().Subject;

        var second = await BeginAsync(factory, UploadSource(archivePath), "restore-second");
        second.Should().BeOfType<RestoreStartOutcome.Failure>()
            .Which.Code.Should().Be(ErrorCodes.RestoreInProgress);

        var backup = await WithBackupServiceAsync(
            factory.Services,
            service => service.CreateAsync("concurrent backup", null, TestIp, Ct));
        backup.Should().BeOfType<BackupCreateOutcome.Failure>()
            .Which.Code.Should().Be(ErrorCodes.BackupInProgress);

        var execution = Restores(factory).ExecuteAsync(accepted.Operation, Ct);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        factory.Services.GetRequiredService<RestoreState>().IsMaintenanceActive.Should().BeTrue();

        runner.Release.TrySetResult();
        (await execution).Should().BeOfType<RestoreExecutionOutcome.Success>();
    }

    // ------------------------------------------------------------------
    // Failure and rollback
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_pg_restore_failure_leaves_the_original_data_intact()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);
        await SeedUserAndSessionAsync();

        var runner = RecordingPgRestoreRunner.Failing("pg_restore: error: could not execute query");
        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(dataDir, runner, signal);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        var accepted = (await BeginAsync(factory, UploadSource(archivePath), "restore-failure"))
            .Should().BeOfType<RestoreStartOutcome.Accepted>().Subject;

        var execution = await Restores(factory).ExecuteAsync(accepted.Operation, Ct);

        var failure = execution.Should().BeOfType<RestoreExecutionOutcome.Failure>().Subject;
        failure.Code.Should().Be(ErrorCodes.RestoreFailed);
        failure.RestoreId.Should().Be("restore-failure");

        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(OriginalImage, "the file swap never ran");
        File.ReadAllBytes(Path.Combine(dataDir, "config", "dp-keys", "key-abc.xml"))
            .Should().Equal(OriginalDataProtectionKey);

        RestoreMarker.Exists(configuration).Should().BeFalse();
        signal.Invocations.Should().Be(0);

        await using (var context = fixture.CreateContext())
        {
            (await context.UserSessions.AsNoTracking().CountAsync(Ct)).Should()
                .Be(1, "the failure happened before session revocation");
            (await context.AuditEvents.AsNoTracking()
                    .CountAsync(entry => entry.EventType == AuditEventTypes.BackupRestored, Ct))
                .Should().Be(0);
        }

        var state = factory.Services.GetRequiredService<RestoreState>();
        state.IsMaintenanceActive.Should().BeFalse();
        state.Phase.Should().Be("failed");
        state.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task A_file_swap_failure_after_the_commit_still_marks_and_requests_the_restart()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);

        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(dataDir, RecordingPgRestoreRunner.Succeeding(), signal);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        var accepted = (await BeginAsync(factory, UploadSource(archivePath), "restore-swap-failure"))
            .Should().BeOfType<RestoreStartOutcome.Accepted>().Subject;

        // Make the second rename impossible: config/dp-keys becomes a file, so
        // moving the staged directory onto it fails after images/ was already
        // placed, which forces the rollback path. The database restore itself
        // already committed before the swap ran.
        var dataProtectionDirectory = Path.Combine(dataDir, "config", "dp-keys");
        Directory.Delete(dataProtectionDirectory, recursive: true);
        await File.WriteAllTextAsync(dataProtectionDirectory, "not a directory", Ct);

        var execution = await Restores(factory).ExecuteAsync(accepted.Operation, Ct);

        // The files were rolled back, but the database is already the archive's:
        // the restore must not claim otherwise and must still restart.
        var applied = execution.Should()
            .BeOfType<RestoreExecutionOutcome.AppliedRestartRequired>().Subject;
        applied.RestoreId.Should().Be("restore-swap-failure");
        applied.Error.Should().Contain("restored database was applied");

        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(OriginalImage, "the rollback restores the original images");
        File.Exists(dataProtectionDirectory).Should().BeTrue(
            "the failed target was never a directory and is left untouched");

        RestoreMarker.Exists(configuration).Should().BeTrue(
            "the marker makes the next start apply migrations and report the outcome");
        var marker = RestoreMarker.TryRead(configuration, NullLogger());
        marker.Should().NotBeNull();
        marker!.RestoreId.Should().Be("restore-swap-failure");
        marker.PostStepError.Should().NotBeNullOrWhiteSpace();
        signal.Invocations.Should().Be(1, "the process must stop so migrations run");

        var state = factory.Services.GetRequiredService<RestoreState>();
        state.IsMaintenanceActive.Should().BeFalse();
        state.Phase.Should().Be("failed");
        state.LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task A_pre_commit_pg_restore_failure_removes_the_upload_and_keeps_the_original_data()
    {
        var dataDir = SeedDataDirectory(RecoveryKey);

        var runner = RecordingPgRestoreRunner.Failing("pg_restore: error: could not execute query");
        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(dataDir, runner, signal);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        // An upload lives in the restore's private working directory, the same
        // place the endpoint streams it to.
        var uploadPath = RestoreService.UploadPath(configuration, "restore-upload-failure");
        Directory.CreateDirectory(Path.GetDirectoryName(uploadPath)!);
        var archivePath = await WriteArchiveAsync(dataDir);
        File.Move(archivePath, uploadPath);
        var upload = new RestoreSource.Upload(
            uploadPath,
            RestoreService.UploadFileName,
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(uploadPath, Ct))));

        var accepted = (await BeginAsync(factory, upload, "restore-upload-failure"))
            .Should().BeOfType<RestoreStartOutcome.Accepted>().Subject;

        var execution = await Restores(factory).ExecuteAsync(accepted.Operation, Ct);

        execution.Should().BeOfType<RestoreExecutionOutcome.Failure>()
            .Which.Code.Should().Be(ErrorCodes.RestoreFailed);

        RestoreMarker.Exists(configuration).Should().BeFalse("nothing was committed");
        signal.Invocations.Should().Be(0);
        File.Exists(uploadPath).Should().BeFalse("the failed upload is reclaimed, not left behind");
        Directory.Exists(Path.Combine(
                RestoreService.RestoreDirectory(configuration, "restore-upload-failure"),
                "staging"))
            .Should().BeFalse();
        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(OriginalImage);
    }

    // ------------------------------------------------------------------
    // HTTP surface
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_restore_route_is_admin_only_and_requires_the_typed_confirmation()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await SeedUserAsync("member", MemberPassword, UserRole.Member);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);
        var row = await SeedBackupRowAsync(dataDir, archivePath);

        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            new RecordingRestartSignal());

        using var anonymous = factory.CreateClient();
        (await SendJsonAsync(anonymous, HttpMethod.Post, RestorePath, RestoreBody(row.Id), csrfToken: null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var member = await SignInAsync(factory, "member", MemberPassword);
        (await SendJsonAsync(member.Client, HttpMethod.Post, RestorePath, RestoreBody(row.Id), member.CsrfToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var wrongConfirmation = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            RestorePath,
            JsonSerializer.Serialize(new
            {
                backup_id = row.Id,
                reason = Reason,
                confirmation = "restore",
            }),
            admin.CsrfToken);
        wrongConfirmation.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(wrongConfirmation)).Should().Be(ErrorCodes.ConfirmationMismatch);

        var noReason = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            RestorePath,
            JsonSerializer.Serialize(new { backup_id = row.Id, confirmation = "RESTORE" }),
            admin.CsrfToken);
        noReason.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorCodeAsync(noReason)).Should().Be(ErrorCodes.ReasonRequired);

        var missing = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            RestorePath,
            RestoreBody(987654),
            admin.CsrfToken);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorCodeAsync(missing)).Should().Be(ErrorCodes.BackupNotFound);
    }

    [Fact]
    public async Task The_restore_route_answers_202_and_revokes_every_session()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);
        var row = await SeedBackupRowAsync(dataDir, archivePath);

        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            signal);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        using var admin = await SignInAsync(factory, "root", AdminPassword);

        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            RestorePath,
            RestoreBody(row.Id),
            admin.CsrfToken);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("restore_id").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("backup_id").GetInt64().Should().Be(row.Id);
        payload.GetProperty("filename").GetString().Should().Be(row.Filename);
        payload.GetProperty("safety_backup_filename").GetString().Should().NotBeNullOrWhiteSpace();
        var safetyFilename = payload.GetProperty("safety_backup_filename").GetString()!;

        await AwaitExecutionAsync(factory);

        signal.Invocations.Should().Be(1);
        RestoreMarker.Exists(configuration).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(RestoredImage);
        File.Exists(Path.Combine(dataDir, "backups", "safety", safetyFilename)).Should().BeTrue();

        // Every session was revoked, including the caller's own.
        (await GetAsync(admin.Client, BackupsPath)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_restore_route_accepts_a_multipart_upload()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir, fileName: "uploaded-archive.ffbackup");

        var signal = new RecordingRestartSignal();
        using var factory = CreateFactory(
            dataDir,
            RecordingPgRestoreRunner.Succeeding(),
            signal);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        using var admin = await SignInAsync(factory, "root", AdminPassword);

        using var content = new MultipartFormDataContent();
        await using (var archive = File.OpenRead(archivePath))
        {
            content.Add(new StreamContent(archive), "file", "uploaded-archive.ffbackup");
            content.Add(new StringContent(Reason), "reason");
            content.Add(new StringContent("RESTORE"), "confirmation");

            using var request = new HttpRequestMessage(HttpMethod.Post, RestorePath)
            {
                Content = content,
            };
            request.Headers.Add(CsrfMiddleware.HeaderName, admin.CsrfToken);
            request.Headers.TryAddWithoutValidation(TestRemoteIpStartupFilter.HeaderName, TestIp);

            var response = await admin.Client.SendAsync(request, Ct);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var payload = await ReadJsonAsync(response);
            payload.GetProperty("backup_id").ValueKind.Should().Be(JsonValueKind.Null);
            payload.GetProperty("filename").GetString().Should().Be("uploaded-archive.ffbackup");
            payload.GetProperty("restore_id").GetString().Should().NotBeNullOrWhiteSpace();
        }

        await AwaitExecutionAsync(factory);

        signal.Invocations.Should().Be(1);
        RestoreMarker.Exists(configuration).Should().BeTrue();
        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(RestoredImage);
    }

    [Fact]
    public async Task Maintenance_mode_answers_503_for_other_routes_and_keeps_health_available()
    {
        await SeedHouseholdAsync(AuthMode.Multiuser);
        await SeedUserAsync("root", AdminPassword, UserRole.Admin);
        await MarkSetupCompletedAsync();

        var dataDir = SeedDataDirectory(RecoveryKey);
        var archivePath = await WriteArchiveAsync(dataDir);
        var row = await SeedBackupRowAsync(dataDir, archivePath);

        var runner = new GatedPgRestoreRunner();
        using var factory = CreateFactory(dataDir, runner, new RecordingRestartSignal());

        using var admin = await SignInAsync(factory, "root", AdminPassword);
        var response = await SendJsonAsync(
            admin.Client,
            HttpMethod.Post,
            RestorePath,
            RestoreBody(row.Id),
            admin.CsrfToken);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using var other = factory.CreateClient();
        var blocked = await other.GetAsync(BackupsPath, Ct);
        blocked.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ReadErrorCodeAsync(blocked)).Should().Be(ErrorCodes.RestoreInProgress);

        (await other.GetAsync("/api/v1/health", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        runner.Release.TrySetResult();
        await AwaitExecutionAsync(factory);

        // Maintenance mode is cleared once the restore finishes.
        (await other.GetAsync(BackupsPath, Ct)).StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The only test that touches real client tools. It is skipped (with the
    /// reason visible in the run) when <c>pg_restore</c> is absent; the real
    /// restore path is verified in the runtime image at M13-10.
    /// </summary>
    [Fact]
    public async Task Real_pg_restore_is_skipped_when_the_client_tools_are_absent()
    {
        var available = await PgRestoreVersionAvailableAsync();
        if (!available)
        {
            Assert.Skip(
                "pg_restore is not installed on this workstation; the real restore path is " +
                "verified in the runtime image at M13-10.");
            return;
        }

        var versionOutput = await new PgDumpVersionReader().ReadVersionOutputAsync(Ct);
        Assert.Skip(
            "pg_restore is present but the real dump/restore round trip is part of the M13-10 " +
            $"image gate (pg_dump probe: {versionOutput ?? "unavailable"}).");
    }

    [Fact]
    public async Task Terminating_connections_drops_every_other_session_on_the_target_database()
    {
        var runner = new PgRestoreRunner(
            fixture.ConnectionString,
            PgRestoreRunner.DefaultExecutablePath,
            TimeSpan.FromMinutes(1),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PgRestoreRunner>.Instance);

        await using var victim = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await victim.OpenAsync(Ct);
        await using (var command = victim.CreateCommand())
        {
            command.CommandText = "SELECT 1";
            (await command.ExecuteScalarAsync(Ct)).Should().Be(1);
        }

        await runner.TerminateConnectionsAsync(Ct);

        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var command = victim.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(Ct);
        });
        exception.Should().NotBeNull();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Microsoft.Extensions.Logging.ILogger NullLogger() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

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

    private static string RestoreBody(long backupId) => JsonSerializer.Serialize(new
    {
        backup_id = backupId,
        reason = Reason,
        confirmation = "RESTORE",
    });

    private static RestoreSource.Upload UploadSource(string archivePath) =>
        new(
            archivePath,
            Path.GetFileName(archivePath),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archivePath))));

    private static RestoreService Restores(ApiFactory factory) =>
        factory.Services.GetRequiredService<RestoreService>();

    private static async Task AwaitExecutionAsync(ApiFactory factory)
    {
        var state = factory.Services.GetRequiredService<RestoreState>();
        await state.Execution.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        state.IsMaintenanceActive.Should().BeFalse();
    }

    private static async Task<RestoreStartOutcome> BeginAsync(
        ApiFactory factory,
        RestoreSource source,
        string restoreId) =>
        await Restores(factory).BeginAsync(source, Reason, actorUserId: null, TestIp, restoreId, Ct);

    private static async Task<T> WithBackupServiceAsync<T>(
        IServiceProvider provider,
        Func<BackupService, Task<T>> action)
    {
        using var scope = provider.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<BackupService>());
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

        await using (var context = fixture.CreateContext())
        {
            (await context.BackupMetadata.AsNoTracking().CountAsync(Ct)).Should()
                .Be(0, "no safety backup is created before validation passes");
        }

        File.ReadAllBytes(Path.Combine(dataDir, "images", "recipes", "pasta.jpg"))
            .Should().Equal(OriginalImage);
    }

    private ApiFactory CreateFactory(
        string dataDir,
        IPgRestoreRunner runner,
        IRestoreRestartSignal signal,
        RecordingLoggerProvider? recorder = null) =>
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

            if (recorder is not null)
            {
                services.AddLogging(logging => logging.AddProvider(recorder));
            }
        });

    /// <summary>
    /// A data directory in the mounted <c>/data</c> layout: the original recipe
    /// image, the original Data Protection key, and (optionally) the recovery
    /// key.
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
    /// Writes a real encrypted archive containing the dump, one recipe image,
    /// and one Data Protection key, with the given manifest schema version.
    /// </summary>
    private async Task<string> WriteArchiveAsync(
        string dataDir,
        string? schemaVersion = null,
        string fileName = "fatefridge-restore-test.ffbackup")
    {
        var path = Path.Combine(dataDir, "backups", fileName);
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

    private async Task<BackupMetadata> SeedBackupRowAsync(
        string dataDir,
        string archivePath,
        string? checksumOverride = null)
    {
        var filename = Path.GetFileName(archivePath);
        _ = dataDir;
        var schemaVersion = await LatestSchemaVersionAsync();

        await using var context = fixture.CreateContext();
        var row = new BackupMetadata
        {
            Filename = filename,
            AppVersion = AppInfo.Version,
            SchemaVersion = schemaVersion,
            CreatedAt = Now,
            Checksum = checksumOverride
                ?? Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archivePath, Ct))),
            SizeBytes = new FileInfo(archivePath).Length,
            Kind = BackupKind.Manual,
            Status = BackupStatus.Completed,
        };

        context.BackupMetadata.Add(row);
        await context.SaveChangesAsync(Ct);
        return row;
    }

    private async Task SeedUserAndSessionAsync()
    {
        await using var context = fixture.CreateContext();
        var user = new User
        {
            Username = "session-owner",
            NormalizedUsername = UsernameNormalizer.Normalize("session-owner"),
            PasswordHash = new Argon2idPasswordHasher().Hash(MemberPassword),
            Role = UserRole.Admin,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        };
        context.Users.Add(user);
        await context.SaveChangesAsync(Ct);

        context.UserSessions.Add(new UserSession
        {
            UserId = user.Id,
            TokenHash = RandomNumberGenerator.GetBytes(32),
            CreatedAt = SeedTimestamp,
            IdleExpiresAt = SeedTimestamp.AddHours(12),
            AbsoluteExpiresAt = SeedTimestamp.AddDays(7),
            LastSeenAt = SeedTimestamp,
            IpAddress = TestIp,
            UserAgent = TestUserAgent,
        });
        await context.SaveChangesAsync(Ct);
    }

    private async Task SeedHouseholdAsync(AuthMode authMode)
    {
        await using var context = fixture.CreateContext();
        context.HouseholdSettings.Add(new HouseholdSettings
        {
            Id = 1,
            HouseholdName = "M13 Restore Household",
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
        context.Users.Add(new User
        {
            Username = username,
            NormalizedUsername = UsernameNormalizer.Normalize(username),
            PasswordHash = new Argon2idPasswordHasher().Hash(password),
            Role = role,
            IsActive = true,
            CreatedAt = SeedTimestamp,
            UpdatedAt = SeedTimestamp,
        });
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
