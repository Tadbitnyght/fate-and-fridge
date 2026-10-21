using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FateAndFridge.Api.Backup;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FateAndFridge.Api.Services;

/// <summary>Where a restore's archive comes from (docs/API.md §18).</summary>
public abstract record RestoreSource
{
    private RestoreSource()
    {
    }

    /// <summary>An archive already recorded in <c>backup_metadata</c>.</summary>
    public sealed record StoredBackup(long BackupId) : RestoreSource;

    /// <summary>
    /// An archive uploaded with the request and written to a private restore
    /// directory. <paramref name="Sha256"/> is the digest computed while the
    /// upload streamed to disk.
    /// </summary>
    public sealed record Upload(string ArchivePath, string Filename, string Sha256) : RestoreSource;
}

/// <summary>Result of the synchronous restore validation (docs/API.md §18).</summary>
public abstract record RestoreStartOutcome
{
    private RestoreStartOutcome()
    {
    }

    /// <summary>
    /// Validation, archive verification, and the safety backup succeeded. The
    /// caller must either run <see cref="RestoreService.ExecuteAsync"/> (or
    /// <see cref="RestoreService.StartInBackground"/>) or release the operation
    /// gate; nothing has been mutated yet.
    /// </summary>
    public sealed record Accepted(RestoreOperation Operation) : RestoreStartOutcome;

    public sealed record Failure(int StatusCode, string Code, string Message) : RestoreStartOutcome;
}

/// <summary>Result of the background restore (logged by the endpoint).</summary>
public abstract record RestoreExecutionOutcome
{
    private RestoreExecutionOutcome()
    {
    }

    public sealed record Success(string RestoreId) : RestoreExecutionOutcome;

    public sealed record Failure(string RestoreId, string Code, string Message) : RestoreExecutionOutcome;
}

/// <summary>
/// One accepted restore. It carries the decrypted recovery key in memory for
/// the background phase only; it is a plain class (not a record) so no generated
/// <c>ToString</c> can ever print the key, and the key is zeroed when the
/// background phase ends.
/// </summary>
public sealed class RestoreOperation
{
    internal RestoreOperation(
        string restoreId,
        long? backupId,
        string backupFilename,
        string safetyBackupFilename,
        string archivePath,
        string expectedSha256,
        byte[] recoveryKey,
        string reason,
        long? actorUserId,
        string? ipAddress)
    {
        RestoreId = restoreId;
        BackupId = backupId;
        BackupFilename = backupFilename;
        SafetyBackupFilename = safetyBackupFilename;
        ArchivePath = archivePath;
        ExpectedSha256 = expectedSha256;
        RecoveryKey = recoveryKey;
        Reason = reason;
        ActorUserId = actorUserId;
        IpAddress = ipAddress;
    }

    /// <summary>The id returned in the <c>202</c> response.</summary>
    public string RestoreId { get; }

    /// <summary>The row id when a stored archive was chosen; null for an upload.</summary>
    public long? BackupId { get; }

    /// <summary>The archive's file name (server-generated, or the upload's name).</summary>
    public string BackupFilename { get; }

    /// <summary>The safety archive created before any mutation.</summary>
    public string SafetyBackupFilename { get; }

    internal string ArchivePath { get; }

    internal string ExpectedSha256 { get; }

    internal byte[] RecoveryKey { get; }

    internal string Reason { get; }

    internal long? ActorUserId { get; }

    internal string? IpAddress { get; }
}

/// <summary>
/// The M13-04 restore orchestration (docs/M13_PLAN.md §1.9, §2.1, §4;
/// docs/SECURITY.md §12; docs/API.md §18).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BeginAsync"/> does every step that must happen **before** any
/// mutation: it takes the same operation gate as backup creation, resolves the
/// archive, reads and verifies it with the recovery key (header, GCM
/// authentication, ciphertext checksum, entry checksums against the manifest),
/// refuses an archive whose schema version this build does not know, and only
/// then creates the mandatory safety backup. A validation failure therefore
/// leaves no row, no file, no maintenance mode, and no marker behind.
/// </para>
/// <para>
/// <see cref="ExecuteAsync"/> runs afterwards (the endpoint has already answered
/// <c>202</c>): extraction into <c>{DataDir}/backups/.restore/&lt;id&gt;/staging</c>,
/// maintenance mode, connection termination plus
/// <c>pg_restore --clean --if-exists --no-owner --no-privileges
/// --single-transaction</c>, session revocation, the <c>backup.restored</c>
/// audit row written into the restored database with raw SQL, the
/// <c>images/</c> and <c>config/dp-keys/</c> swap (with rollback), the restore
/// marker, and a graceful stop.
/// </para>
/// <para>
/// Single instance only: the maintenance flag and the operation gate are
/// process-local, which matches the one-container deployment
/// (docs/M13_PLAN.md §1.18).
/// </para>
/// </remarks>
public sealed class RestoreService(
    IServiceScopeFactory scopeFactory,
    IPgRestoreRunner restoreRunner,
    BackupOperationGate operationGate,
    RestoreState state,
    IRestoreRestartSignal restartSignal,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<RestoreService> logger)
{
    /// <summary>Maximum accepted archive upload (docs/M13_PLAN.md §1.9).</summary>
    public const long MaximumUploadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>The tar entry that carries the PostgreSQL custom-format dump.</summary>
    public const string DatabaseDumpEntryName = "database.dump";

    /// <summary>Working directory name under <c>{DataDir}/backups</c>.</summary>
    public const string RestoreDirectoryName = ".restore";

    /// <summary>File name an uploaded archive is streamed to.</summary>
    public const string UploadFileName = "upload.ffbackup";

    /// <summary>Absolute <c>{DataDir}/backups/.restore</c>.</summary>
    public static string RestoreRootDirectory(IConfiguration configuration) =>
        Path.Combine(BackupService.ArchivesDirectory(configuration), RestoreDirectoryName);

    /// <summary>Absolute working directory of one restore.</summary>
    public static string RestoreDirectory(IConfiguration configuration, string restoreId) =>
        Path.Combine(RestoreRootDirectory(configuration), restoreId);

    /// <summary>Absolute path an uploaded archive is streamed to.</summary>
    public static string UploadPath(IConfiguration configuration, string restoreId) =>
        Path.Combine(RestoreDirectory(configuration, restoreId), UploadFileName);

    /// <summary>
    /// A fresh restore id: the UTC second plus 16 random hex characters, safe in
    /// a log line, a directory name, and the <c>202</c> body.
    /// </summary>
    public string NewRestoreId()
    {
        var timestamp = timeProvider
            .GetUtcNow()
            .ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        return $"{timestamp}-{RandomNumberGenerator.GetHexString(16, lowercase: true)}";
    }

    /// <summary>
    /// Validates one restore and creates its safety backup. No mutation of the
    /// application data happens here; when the result is
    /// <see cref="RestoreStartOutcome.Accepted"/> the caller owns the operation
    /// gate until the background phase finishes.
    /// </summary>
    public async Task<RestoreStartOutcome> BeginAsync(
        RestoreSource source,
        string reason,
        long? actorUserId,
        string? ipAddress,
        string restoreId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);

        if (!operationGate.TryEnter())
        {
            return new RestoreStartOutcome.Failure(
                StatusCodes.Status409Conflict,
                ErrorCodes.RestoreInProgress,
                "A backup or restore is already running. Try again when it finishes.");
        }

        var handedOff = false;
        try
        {
            state.Begin(restoreId, timeProvider.GetUtcNow());

            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            long? backupId = null;
            string archivePath;
            string filename;
            string? expectedSha256;

            switch (source)
            {
                case RestoreSource.StoredBackup stored:
                    {
                        var row = await dbContext.BackupMetadata
                            .AsNoTracking()
                            .SingleOrDefaultAsync(candidate => candidate.Id == stored.BackupId, cancellationToken)
                            .ConfigureAwait(false);

                        if (row is null || row.Status == BackupStatus.Deleted)
                        {
                            return Failure(
                                StatusCodes.Status404NotFound,
                                ErrorCodes.BackupNotFound,
                                "The backup does not exist.");
                        }

                        if (row.Status == BackupStatus.Pending)
                        {
                            return Failure(
                                StatusCodes.Status409Conflict,
                                ErrorCodes.BackupInProgress,
                                "A backup that is still being created cannot be restored.");
                        }

                        archivePath = BackupService.ResolveArchivePath(configuration, row);
                        if (!File.Exists(archivePath))
                        {
                            return Failure(
                                StatusCodes.Status404NotFound,
                                ErrorCodes.BackupFileMissing,
                                "The backup file is missing from the backups directory.");
                        }

                        backupId = row.Id;
                        filename = row.Filename;
                        expectedSha256 = string.IsNullOrWhiteSpace(row.Checksum) ? null : row.Checksum;
                        break;
                    }

                case RestoreSource.Upload upload:
                    {
                        if (!File.Exists(upload.ArchivePath))
                        {
                            return Failure(
                                StatusCodes.Status400BadRequest,
                                ErrorCodes.ValidationFailed,
                                "The uploaded archive could not be found.");
                        }

                        archivePath = upload.ArchivePath;
                        filename = upload.Filename;
                        expectedSha256 = upload.Sha256;
                        break;
                    }

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(source),
                        source,
                        "Unknown restore source.");
            }

            var recoveryKeyResult = await ReadRecoveryKeyAsync(scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);
            if (recoveryKeyResult.Failure is not null)
            {
                return recoveryKeyResult.Failure;
            }

            var recoveryKey = recoveryKeyResult.Key!;
            var validated = await ValidateArchiveAsync(
                    archivePath,
                    recoveryKey,
                    expectedSha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (validated.Failure is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryKey);
                return validated.Failure;
            }

            var schemaFailure = CheckSchemaVersionAsync(
                dbContext,
                validated.Manifest!.SchemaVersion);
            if (schemaFailure is not null)
            {
                CryptographicOperations.ZeroMemory(recoveryKey);
                return schemaFailure;
            }

            // Step 4: the safety backup exists and is verified before anything
            // is touched (docs/M13_PLAN.md §1.9 step 2, §4).
            state.MarkPhase("safety_backup");
            var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
            var safety = await backups
                .CreateSafetyBackupAsync(reason, actorUserId, ipAddress, cancellationToken)
                .ConfigureAwait(false);

            if (safety is not BackupCreateOutcome.Success { Backup.Status: BackupStatus.Completed } created)
            {
                CryptographicOperations.ZeroMemory(recoveryKey);

                return safety is BackupCreateOutcome.Failure failure
                    && failure.StatusCode != StatusCodes.Status500InternalServerError
                    ? new RestoreStartOutcome.Failure(failure.StatusCode, failure.Code, failure.Message)
                    : Failure(
                        StatusCodes.Status500InternalServerError,
                        ErrorCodes.RestoreFailed,
                        "The safety backup could not be created; the restore was aborted and no data was touched.");
            }

            state.MarkPhase("accepted");
            handedOff = true;

            return new RestoreStartOutcome.Accepted(new RestoreOperation(
                restoreId,
                backupId,
                filename,
                created.Backup.Filename,
                archivePath,
                expectedSha256 ?? string.Empty,
                recoveryKey,
                reason,
                actorUserId,
                ipAddress));
        }
        finally
        {
            if (!handedOff)
            {
                operationGate.Exit();
            }
        }
    }

    /// <summary>
    /// Runs the background half of an accepted restore and always releases the
    /// operation gate. A failure leaves the original data intact (the database
    /// restore is one transaction and the file swap rolls back), clears
    /// maintenance mode, records the failure in <see cref="RestoreState"/>, and
    /// returns a mapped error.
    /// </summary>
    public async Task<RestoreExecutionOutcome> ExecuteAsync(
        RestoreOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var restoreId = operation.RestoreId;
        var restoreDirectory = RestoreDirectory(configuration, restoreId);
        var stagingDirectory = Path.Combine(restoreDirectory, "staging");

        try
        {
            state.MarkPhase("extracting");
            var manifest = await ExtractAsync(operation, stagingDirectory, cancellationToken)
                .ConfigureAwait(false);

            state.EnterMaintenance(restoreId);

            state.MarkPhase("restoring");
            await restoreRunner.TerminateConnectionsAsync(cancellationToken).ConfigureAwait(false);
            await restoreRunner
                .RestoreAsync(Path.Combine(stagingDirectory, DatabaseDumpEntryName), cancellationToken)
                .ConfigureAwait(false);

            // Step 8: every client must sign in again.
            state.MarkPhase("sessions");
            await RevokeSessionsAsync(cancellationToken).ConfigureAwait(false);

            // Step 9: audit into the restored database, with raw SQL so it works
            // even when the restored schema is older than this build.
            state.MarkPhase("audit");
            await WriteRestoreAuditAsync(operation, manifest, cancellationToken).ConfigureAwait(false);

            state.MarkPhase("files");
            SwapLiveDirectories(restoreDirectory, restoreId, stagingDirectory);

            state.MarkPhase("retention");
            await PruneSafetyBackupsAsync(cancellationToken).ConfigureAwait(false);

            state.MarkPhase("marker");
            RestoreMarker.Write(
                configuration,
                new RestoreMarkerDocument
                {
                    RestoreId = restoreId,
                    BackupFilename = operation.BackupFilename,
                    SafetyBackupFilename = operation.SafetyBackupFilename,
                    AppVersion = AppInfo.Version,
                    SchemaVersion = manifest.SchemaVersion,
                    CompletedAt = timeProvider.GetUtcNow(),
                },
                logger);

            state.MarkPhase("restarting");
            restartSignal.RequestRestart();

            state.ExitMaintenance();
            state.MarkCompleted();
            logger.LogInformation(
                "Restore {RestoreId} completed; the application is restarting into the restored state.",
                restoreId);

            return new RestoreExecutionOutcome.Success(restoreId);
        }
        catch (RestoreFailureException failure)
        {
            return Fail(operation, failure.Code, failure.Message, exception: null, stagingDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(
                operation,
                ErrorCodes.RestoreFailed,
                "The restore was cancelled; the original data is intact.",
                exception: null,
                stagingDirectory);
        }
        catch (Exception exception)
        {
            return Fail(
                operation,
                ErrorCodes.RestoreFailed,
                "The restore failed; the original data is intact. See the application log for the recorded error.",
                exception,
                stagingDirectory);
        }
        finally
        {
            state.ExitMaintenance();
            CryptographicOperations.ZeroMemory(operation.RecoveryKey);
            operationGate.Exit();
        }
    }

    /// <summary>
    /// Starts <see cref="ExecuteAsync"/> on the thread pool. The execution task
    /// is recorded before this method returns, so an observer can await the
    /// outcome after the <c>202</c> response.
    /// </summary>
    public void StartInBackground(RestoreOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var execution = Task.Run(
            async () =>
            {
                try
                {
                    await ExecuteAsync(operation, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "The background restore {RestoreId} failed unexpectedly.",
                        operation.RestoreId);
                }
            },
            CancellationToken.None);

        state.BeginExecution(execution);
    }

    private RestoreExecutionOutcome Fail(
        RestoreOperation operation,
        string code,
        string message,
        Exception? exception,
        string stagingDirectory)
    {
        state.ExitMaintenance();
        state.MarkFailed(message);

        if (exception is null)
        {
            logger.LogError(
                "Restore {RestoreId} failed ({Code}): {Message}",
                operation.RestoreId,
                code,
                message);
        }
        else
        {
            logger.LogError(
                exception,
                "Restore {RestoreId} failed ({Code}): {Message}",
                operation.RestoreId,
                code,
                message);
        }

        RemoveStaging(stagingDirectory);
        return new RestoreExecutionOutcome.Failure(operation.RestoreId, code, message);
    }

    private static RestoreStartOutcome.Failure Failure(int statusCode, string code, string message) =>
        new(statusCode, code, message);

    private static RestoreStartOutcome.Failure KeyMissing() =>
        Failure(
            StatusCodes.Status409Conflict,
            ErrorCodes.BackupKeyMissing,
            "Restore is unavailable: the recovery key file is missing or unusable. " +
            "The archive can only be decrypted with the key it was created with.");

    private static async Task<(byte[]? Key, RestoreStartOutcome.Failure? Failure)> ReadRecoveryKeyAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var keyStore = services.GetRequiredService<RecoveryKeyStore>();

        string? keyText;
        try
        {
            keyText = await keyStore.TryReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return (null, KeyMissing());
        }

        if (keyText is null)
        {
            return (null, KeyMissing());
        }

        byte[] key;
        try
        {
            key = Base64Url.DecodeFromChars(keyText);
        }
        catch (FormatException)
        {
            return (null, KeyMissing());
        }

        if (key.Length != BackupArchiveFormat.RecoveryKeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            return (null, KeyMissing());
        }

        return (key, null);
    }

    /// <summary>
    /// Opens the archive with the recovery key, authenticates every record,
    /// checks the ciphertext SHA-256 when one is known, and verifies every entry
    /// against the manifest. Nothing is written.
    /// </summary>
    private static async Task<(BackupManifest? Manifest, RestoreStartOutcome.Failure? Failure)>
        ValidateArchiveAsync(
            string archivePath,
            byte[] recoveryKey,
            string? expectedSha256,
            CancellationToken cancellationToken)
    {
        try
        {
            await using var input = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var reader = await BackupArchiveReader
                .OpenAsync(input, recoveryKey, expectedSha256, cancellationToken)
                .ConfigureAwait(false);

            await foreach (var _ in reader.ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
            {
                // Enumerating is the verification: the reader authenticates every
                // record and compares every entry with the manifest.
            }

            if (!reader.IsVerified || reader.Manifest is null)
            {
                return (null, Failure(
                    StatusCodes.Status400BadRequest,
                    ErrorCodes.BackupIntegrityFailed,
                    "The backup archive could not be verified."));
            }

            return (reader.Manifest, null);
        }
        catch (BackupArchiveException exception)
        {
            var (code, message) = DescribeArchiveFailure(exception);
            return (null, Failure(StatusCodes.Status400BadRequest, code, message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (null, Failure(
                StatusCodes.Status400BadRequest,
                ErrorCodes.BackupIntegrityFailed,
                "The backup archive could not be read."));
        }
    }

    /// <summary>
    /// Refuses an archive this build cannot restore into: the manifest's
    /// <c>schema_version</c> must be one of the migrations compiled into this
    /// application, and must not be newer than the latest one
    /// (docs/M13_PLAN.md §1.9 step 1, §4).
    /// </summary>
    private static RestoreStartOutcome.Failure? CheckSchemaVersionAsync(
        AppDbContext dbContext,
        string schemaVersion)
    {
        var known = dbContext.Database.GetMigrations();

        if (known.Contains(schemaVersion, StringComparer.Ordinal))
        {
            return null;
        }

        var latest = known.LastOrDefault();
        if (TryReadMigrationTimestamp(schemaVersion, out var archivedTimestamp)
            && TryReadMigrationTimestamp(latest, out var latestTimestamp)
            && archivedTimestamp > latestTimestamp)
        {
            return Failure(
                StatusCodes.Status400BadRequest,
                ErrorCodes.BackupSchemaTooNew,
                $"The archive was created by a newer version of Fate & Fridge " +
                $"(schema {schemaVersion}); this instance can restore up to {latest}.");
        }

        return Failure(
            StatusCodes.Status400BadRequest,
            ErrorCodes.BackupFormatUnsupported,
            $"The archive's schema version '{schemaVersion}' is not a migration known to this application.");
    }

    /// <summary>
    /// Reads the EF Core migration timestamp prefix (<c>yyyyMMddHHmmss</c>) from
    /// a migration id; ids are ordinal and sort by that prefix.
    /// </summary>
    private static bool TryReadMigrationTimestamp(string? migrationId, out long timestamp)
    {
        timestamp = 0;
        if (migrationId is not { Length: >= 14 })
        {
            return false;
        }

        if (migrationId.Length > 14 && migrationId[14] != '_')
        {
            return false;
        }

        return long.TryParse(
            migrationId.AsSpan(0, 14),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out timestamp);
    }

    private static (string Code, string Message) DescribeArchiveFailure(
        BackupArchiveException exception) =>
        exception.Code switch
        {
            BackupArchiveErrorCodes.KeyMismatch => (
                ErrorCodes.BackupKeyMismatch,
                "The recovery key does not match this backup archive."),
            BackupArchiveErrorCodes.IntegrityFailed => (
                ErrorCodes.BackupIntegrityFailed,
                "The backup archive failed its integrity check."),
            _ => (
                ErrorCodes.BackupFormatUnsupported,
                "The file is not a supported Fate & Fridge backup archive."),
        };

    /// <summary>
    /// Streams the verified archive into the staging directory. Entry names are
    /// already rejected for absolute paths, <c>..</c>, duplicates, and links by
    /// the reader; the resolved-path check here is defence in depth
    /// (docs/SECURITY.md §12).
    /// </summary>
    private static async Task<BackupManifest> ExtractAsync(
        RestoreOperation operation,
        string stagingDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);

        BackupManifest manifest;
        try
        {
            await using var input = new FileStream(
                operation.ArchivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await using var reader = await BackupArchiveReader
                .OpenAsync(input, operation.RecoveryKey, operation.ExpectedSha256, cancellationToken)
                .ConfigureAwait(false);

            var stagingRoot = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;

            await foreach (var entry in reader.ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (entry.IsDirectory || entry.IsManifest)
                {
                    continue;
                }

                var target = Path.GetFullPath(Path.Combine(stagingDirectory, entry.Name));
                if (!target.StartsWith(stagingRoot, StringComparison.Ordinal))
                {
                    throw new RestoreFailureException(
                        ErrorCodes.BackupFormatUnsupported,
                        "The backup archive contains an unsafe entry path.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await entry.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }

            if (!reader.IsVerified || reader.Manifest is null)
            {
                throw new RestoreFailureException(
                    ErrorCodes.BackupIntegrityFailed,
                    "The backup archive could not be verified.");
            }

            manifest = reader.Manifest;
        }
        catch (BackupArchiveException exception)
        {
            var (code, message) = DescribeArchiveFailure(exception);
            throw new RestoreFailureException(code, message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new RestoreFailureException(
                ErrorCodes.RestoreFailed,
                "The backup archive could not be extracted into the staging directory.");
        }

        var dumpPath = Path.Combine(stagingDirectory, DatabaseDumpEntryName);
        if (!File.Exists(dumpPath) || new FileInfo(dumpPath).Length == 0)
        {
            throw new RestoreFailureException(
                ErrorCodes.BackupFormatUnsupported,
                "The backup archive does not contain a database dump.");
        }

        return manifest;
    }

    /// <summary>
    /// Deletes every session in the restored database so every client must sign
    /// in again (docs/M13_PLAN.md §1.9 step 7). A restored database that predates
    /// the sessions table has nothing to revoke.
    /// </summary>
    private async Task RevokeSessionsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var revoked = await dbContext.Database
                .ExecuteSqlRawAsync("DELETE FROM user_sessions", cancellationToken)
                .ConfigureAwait(false);

            logger.LogInformation("Revoked {SessionCount} session(s) after the restore.", revoked);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            logger.LogWarning(
                "The restored database has no user_sessions table; no session had to be revoked.");
        }
    }

    /// <summary>
    /// Writes the <c>backup.restored</c> audit row into the **restored**
    /// database with raw SQL, so it works even when the restored schema is
    /// slightly older than this build's entity model
    /// (docs/M13_PLAN.md §1.9 step 7).
    /// </summary>
    private async Task WriteRestoreAuditAsync(
        RestoreOperation operation,
        BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        var metadata = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["restore_id"] = operation.RestoreId,
            ["filename"] = operation.BackupFilename,
            ["safety_backup_filename"] = operation.SafetyBackupFilename,
            ["source"] = operation.BackupId is null ? "upload" : "stored",
            ["app_version"] = manifest.AppVersion,
            ["schema_version"] = manifest.SchemaVersion,
        });

        var after = JsonSerializer.Serialize(new
        {
            restore_id = operation.RestoreId,
            backup_id = operation.BackupId,
            filename = operation.BackupFilename,
        });

        var createdAt = timeProvider.GetUtcNow();

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO audit_events
                        (actor_user_id, event_type, entity_type, entity_id,
                         before_json, after_json, reason, ip_address, created_at, metadata_json)
                    VALUES
                        ({operation.ActorUserId}::bigint,
                         {AuditEventTypes.BackupRestored}::text,
                         {AuditEntityTypes.Backup}::text,
                         {operation.BackupFilename}::text,
                         NULL,
                         {after}::jsonb,
                         {operation.Reason}::text,
                         {operation.IpAddress}::text,
                         {createdAt}::timestamptz,
                         {metadata}::jsonb)
                    """,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // A restored database older than M2 has no audit table; the restore
            // itself is still valid and the log keeps the record.
            logger.LogWarning(
                "The restored database has no audit_events table; the backup.restored row was not written.");
        }
    }

    /// <summary>
    /// Replaces <c>images/</c> and <c>config/dp-keys/</c> with the staged
    /// copies: the live directories are renamed aside first, then the staged
    /// ones are moved into place. Any failure moves everything back
    /// (docs/M13_PLAN.md §1.9 step 6).
    /// </summary>
    private void SwapLiveDirectories(
        string restoreDirectory,
        string restoreId,
        string stagingDirectory)
    {
        var dataRoot = AppPaths.DataRoot(configuration);
        (string Name, string Live, string Staged)[] pairs =
        [
            ("images", Path.Combine(dataRoot, "images"), Path.Combine(stagingDirectory, "images")),
            ("dp-keys", AppPaths.DataProtectionKeysRoot(configuration),
                Path.Combine(stagingDirectory, "config", "dp-keys")),
        ];

        var movedLive = new List<(string Old, string Live)>();
        var placed = new List<(string Live, string Staged)>();

        try
        {
            foreach (var pair in pairs)
            {
                if (!Directory.Exists(pair.Live))
                {
                    continue;
                }

                var old = Path.Combine(restoreDirectory, $"old-{pair.Name}-{restoreId}");
                Directory.Move(pair.Live, old);
                movedLive.Add((old, pair.Live));
            }

            foreach (var pair in pairs)
            {
                if (!Directory.Exists(pair.Staged))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(pair.Live)!);
                Directory.Move(pair.Staged, pair.Live);
                placed.Add((pair.Live, pair.Staged));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                exception,
                "The restored images/config could not be swapped into place; rolling the file swap back.");
            RollbackSwap(placed, movedLive);

            throw new RestoreFailureException(
                ErrorCodes.RestoreFailed,
                "The restored files could not be swapped into place; the previous files were restored.");
        }
    }

    private void RollbackSwap(
        List<(string Live, string Staged)> placed,
        List<(string Old, string Live)> movedLive)
    {
        for (var index = placed.Count - 1; index >= 0; index--)
        {
            var (live, staged) = placed[index];
            try
            {
                if (Directory.Exists(live))
                {
                    Directory.Move(live, staged);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(
                    exception,
                    "The restored directory could not be moved back to staging during rollback.");
            }
        }

        for (var index = movedLive.Count - 1; index >= 0; index--)
        {
            var (old, live) = movedLive[index];
            try
            {
                if (Directory.Exists(old) && !Directory.Exists(live))
                {
                    Directory.Move(old, live);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogCritical(
                    exception,
                    "The original directory {OriginalDirectory} could not be restored during rollback; " +
                    "it is preserved next to the restore working directory.",
                    old);
            }
        }
    }

    /// <summary>
    /// Best-effort last-five retention for safety archives after a successful
    /// restore (docs/M13_PLAN.md §1.8). A restored database that predates
    /// <c>backup_metadata</c> is skipped, and retention never fails the restore.
    /// </summary>
    private async Task PruneSafetyBackupsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
            var pruned = await backups
                .PruneSafetyBackupsAsync(BackupService.SafetyBackupRetention, cancellationToken)
                .ConfigureAwait(false);

            if (pruned > 0)
            {
                logger.LogInformation(
                    "Pruned {PrunedCount} old safety backup(s) after the restore.",
                    pruned);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Safety-backup retention could not run after the restore; it is retried on the next backup.");
        }
    }

    private void RemoveStaging(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The restore staging directory could not be removed.");
        }
    }

    /// <summary>A mapped, already-sanitized restore failure inside the background phase.</summary>
    private sealed class RestoreFailureException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
