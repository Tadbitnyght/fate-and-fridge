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

/// <summary>
/// Who is asking for a restore, and therefore where the archive's recovery key
/// comes from (docs/API.md §18, §21).
/// </summary>
public abstract record RestoreRequester
{
    private RestoreRequester()
    {
    }

    /// <summary>
    /// An Admin session (docs/API.md §18): the key is read from this device's
    /// key file and the caller's reason is audited against the actor.
    /// </summary>
    public sealed record Administrator(string Reason, long? ActorUserId, string? IpAddress) : RestoreRequester;

    /// <summary>
    /// The clean-install setup wizard (docs/M13_PLAN.md §1.10): no session and
    /// no actor exist yet, so the pasted key is the credential. The restore
    /// service takes ownership of the array and zeroes it when the restore
    /// finishes.
    /// </summary>
    public sealed record Setup(byte[] RecoveryKey, string? IpAddress) : RestoreRequester;
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

    /// <summary>
    /// The database restore committed, but a step after the commit failed. The
    /// restored database is already in place, so the process must still stop and
    /// restart (migrations and the startup check run on the next start). The
    /// <c>202</c> response was already sent; this outcome is logged and recorded
    /// in <see cref="RestoreState"/> only.
    /// </summary>
    public sealed record AppliedRestartRequired(string RestoreId, string Error) : RestoreExecutionOutcome;
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

    /// <summary>
    /// The audited reason of a clean-install restore (docs/M13_PLAN.md §1.10):
    /// there is no Admin to type one before setup, and the audit row must still
    /// explain how a system-actor restore happened.
    /// </summary>
    public const string SetupRestoreReason =
        "Clean-install restore of an uploaded backup archive during setup.";

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
        CancellationToken cancellationToken) =>
        await BeginAsync(
                source,
                new RestoreRequester.Administrator(reason, actorUserId, ipAddress),
                restoreId,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// The shared restore sequence for both entry points: the Admin restore and
    /// the pre-setup clean-install restore differ only in where the recovery key
    /// comes from and in who is recorded in the audit row
    /// (docs/M13_PLAN.md §1.9, §1.10).
    /// </summary>
    public async Task<RestoreStartOutcome> BeginAsync(
        RestoreSource source,
        RestoreRequester requester,
        string restoreId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);

        var reason = requester switch
        {
            RestoreRequester.Administrator administrator => administrator.Reason,
            RestoreRequester.Setup => SetupRestoreReason,
            _ => throw new ArgumentOutOfRangeException(
                nameof(requester),
                requester,
                "Unknown restore requester."),
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        // A pre-setup restore has no Admin and no system actor to attribute the
        // safety backup or the audit row to (docs/M13_PLAN.md §1.10).
        long? actorUserId = requester is RestoreRequester.Administrator { ActorUserId: { } actor }
            ? actor
            : null;
        var ipAddress = requester switch
        {
            RestoreRequester.Administrator administrator => administrator.IpAddress,
            RestoreRequester.Setup setup => setup.IpAddress,
            _ => null,
        };

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

            if (requester is RestoreRequester.Setup && source is not RestoreSource.Upload)
            {
                // Before setup there is no backup catalogue to choose from: the
                // archive must come with the request (docs/M13_PLAN.md §1.10).
                return Failure(
                    StatusCodes.Status400BadRequest,
                    ErrorCodes.ValidationFailed,
                    "A clean-install restore accepts an uploaded archive only.");
            }

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

            byte[] recoveryKey;
            if (requester is RestoreRequester.Setup supplied)
            {
                // The pasted key is the credential: it was validated by the
                // caller, and this method owns (and zeroes) the array. There is
                // no device key file to read before setup.
                recoveryKey = supplied.RecoveryKey;
            }
            else
            {
                var recoveryKeyResult = await ReadRecoveryKeyAsync(
                        scope.ServiceProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (recoveryKeyResult.Failure is not null)
                {
                    return recoveryKeyResult.Failure;
                }

                recoveryKey = recoveryKeyResult.Key!;
            }

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

            // The clean-install path adopts the pasted key before the safety
            // backup: a device that has not completed setup has no key file, and
            // the household key is the one the restored instance must keep
            // (docs/M13_PLAN.md §1.10). Validation above has already passed, so
            // nothing is written when the archive or its key is refused.
            if (requester is RestoreRequester.Setup)
            {
                var adoption = await AdoptSuppliedKeyAsync(
                        scope.ServiceProvider,
                        recoveryKey,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (adoption is not null)
                {
                    CryptographicOperations.ZeroMemory(recoveryKey);
                    return adoption;
                }
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
    /// operation gate.
    /// </summary>
    /// <remarks>
    /// Until <c>pg_restore</c> returns, a failure leaves the original data
    /// intact: the database restore is one transaction and the file swap has not
    /// run, so the failure is mapped, maintenance mode is cleared, and the
    /// upload/staging files are removed. Once <c>pg_restore</c> has committed the
    /// database already is the archive's, so no later failure may claim
    /// otherwise: the marker is written and the graceful stop is requested so the
    /// container restarts and migrations run, and the result is
    /// <see cref="RestoreExecutionOutcome.AppliedRestartRequired"/> rather than
    /// <see cref="RestoreExecutionOutcome.Failure"/>.
    /// </remarks>
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
            // ---- Before the database commit: nothing has been replaced. ----

            BackupManifest manifest;
            try
            {
                state.MarkPhase("extracting");
                manifest = await ExtractAsync(operation, stagingDirectory, cancellationToken)
                    .ConfigureAwait(false);

                state.EnterMaintenance(restoreId);

                state.MarkPhase("restoring");
                await restoreRunner.TerminateConnectionsAsync(cancellationToken).ConfigureAwait(false);
                await restoreRunner
                    .RestoreAsync(Path.Combine(stagingDirectory, DatabaseDumpEntryName), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RestoreFailureException failure)
            {
                return Fail(
                    operation,
                    failure.Code,
                    failure.Message,
                    exception: null,
                    restoreDirectory,
                    stagingDirectory);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Fail(
                    operation,
                    ErrorCodes.RestoreFailed,
                    "The restore was cancelled; the original data is intact.",
                    exception: null,
                    restoreDirectory,
                    stagingDirectory);
            }
            catch (Exception exception)
            {
                return Fail(
                    operation,
                    ErrorCodes.RestoreFailed,
                    "The restore failed; the original data is intact. See the application log for the recorded error.",
                    exception,
                    restoreDirectory,
                    stagingDirectory);
            }

            // ---- pg_restore has committed: the database is the archive's. ----

            var markerDocument = new RestoreMarkerDocument
            {
                RestoreId = restoreId,
                BackupFilename = operation.BackupFilename,
                SafetyBackupFilename = operation.SafetyBackupFilename,
                AppVersion = AppInfo.Version,
                SchemaVersion = manifest.SchemaVersion,
                CompletedAt = timeProvider.GetUtcNow(),
            };

            // Directories a failed swap rollback could not move back: they still
            // hold the only copy of the previous files and must not be reclaimed.
            var preservedOriginals = new List<string>();

            var postCommitErrors = new List<string>();

            try
            {
                // Step 8: every client must sign in again. The pool was dropped
                // after pg_restore, so each attempt opens a fresh connection, and a
                // 57P01/transient failure is retried once.
                state.MarkPhase("sessions");
                var revokeError = await RunPostCommitStepAsync(
                        "sessions",
                        RevokeSessionsAsync,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (revokeError is not null)
                {
                    postCommitErrors.Add($"sessions: {revokeError.Message}");
                }

                // Step 9: audit into the restored database, with raw SQL so it works
                // even when the restored schema is older than this build. It is
                // attempted even when the revoke failed: a restore must never
                // silently go unaudited.
                state.MarkPhase("audit");
                var auditError = await RunPostCommitStepAsync(
                        "audit",
                        token => WriteRestoreAuditAsync(operation, manifest, token),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (auditError is not null)
                {
                    postCommitErrors.Add($"audit: {auditError.Message}");
                }

                if (postCommitErrors.Count > 0)
                {
                    // FailAfterCommit writes the marker (with the post_step_error)
                    // and requests the restart, so a committed restore is never
                    // reported as silently successful.
                    throw new InvalidOperationException(
                        $"post_step_error: {string.Join("; ", postCommitErrors)}");
                }

                state.MarkPhase("files");
                SwapLiveDirectories(restoreDirectory, restoreId, stagingDirectory, preservedOriginals);

                state.MarkPhase("retention");
                await PruneSafetyBackupsAsync(cancellationToken).ConfigureAwait(false);

                state.MarkPhase("marker");
                RestoreMarker.Write(configuration, markerDocument, logger);

                // The marker is on disk, so the whole working directory (upload,
                // staging copy, and the swapped-out originals) is reclaimable.
                RemoveDirectory(restoreDirectory);
            }
            catch (Exception exception)
            {
                return FailAfterCommit(
                    operation,
                    markerDocument,
                    exception,
                    restoreDirectory,
                    stagingDirectory,
                    preservedOriginals);
            }

            state.MarkPhase("restarting");
            restartSignal.RequestRestart();

            state.ExitMaintenance();
            state.MarkCompleted();
            logger.LogInformation(
                "Restore {RestoreId} completed; the application is restarting into the restored state.",
                restoreId);

            return new RestoreExecutionOutcome.Success(restoreId);
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
        string restoreDirectory,
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

        RemoveStagingAndUpload(restoreDirectory, stagingDirectory);
        return new RestoreExecutionOutcome.Failure(operation.RestoreId, code, message);
    }

    /// <summary>
    /// Handles a failure after <c>pg_restore</c> committed. The database is
    /// already the archive's, so this never claims the original data is intact:
    /// it records the error, writes the marker (best effort) and requests the
    /// graceful stop that applies migrations on the next start, and only then
    /// clears maintenance mode (docs/M13_PLAN.md §1.9 step 8).
    /// </summary>
    private RestoreExecutionOutcome FailAfterCommit(
        RestoreOperation operation,
        RestoreMarkerDocument document,
        Exception exception,
        string restoreDirectory,
        string stagingDirectory,
        IReadOnlyCollection<string> preservedOriginals)
    {
        var message =
            "The restored database was applied, but a step after the commit failed. " +
            "The application is restarting so migrations run against the restored schema; " +
            "see the application log for the recorded error.";

        logger.LogError(
            exception,
            "Restore {RestoreId} applied the restored database, but a post-commit step failed: {Message}",
            operation.RestoreId,
            message);

        state.MarkFailed(message);

        try
        {
            RestoreMarker.Write(
                configuration,
                document with { PostStepError = Bound(exception.Message) },
                logger);
        }
        catch (Exception markerException) when (markerException is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                markerException,
                "The restore marker for {RestoreId} could not be written after the post-commit failure.",
                operation.RestoreId);
        }

        // The database has committed, so the upload, the staging copy, and the
        // swapped-out originals are reclaimable — except a directory a failed
        // rollback still needs.
        ReclaimRestoreFiles(restoreDirectory, stagingDirectory, preservedOriginals);

        // Keep the phase at "failed": the error must stay visible in the state
        // even though the restart signal is the next thing that happens.
        try
        {
            restartSignal.RequestRestart();
        }
        finally
        {
            state.ExitMaintenance();
        }

        return new RestoreExecutionOutcome.AppliedRestartRequired(operation.RestoreId, message);
    }

    private static string Bound(string message) =>
        message.Length <= 500 ? message : message[..500];

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
    /// Stores the pasted key as this device's recovery key when no key file
    /// exists yet (docs/M13_PLAN.md §1.10). An existing device key is never
    /// overwritten: the restore still runs, and the safety backup is encrypted
    /// with whichever key this device already holds. The key value itself is
    /// never logged.
    /// </summary>
    private async Task<RestoreStartOutcome.Failure?> AdoptSuppliedKeyAsync(
        IServiceProvider services,
        byte[] recoveryKey,
        CancellationToken cancellationToken)
    {
        var keyStore = services.GetRequiredService<RecoveryKeyStore>();

        try
        {
            await keyStore
                .WriteIfAbsentAsync(Base64Url.EncodeToString(recoveryKey), cancellationToken)
                .ConfigureAwait(false);

            return null;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            logger.LogError(
                exception,
                "The recovery key supplied with the clean-install restore could not be stored on this device.");

            return Failure(
                StatusCodes.Status500InternalServerError,
                ErrorCodes.InternalError,
                "The recovery key could not be stored on this device; the restore was aborted " +
                "before any data was touched.");
        }
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
    /// Runs one post-commit step, retrying once after a transient connection
    /// failure with a fresh connection pool. Returns the failure instead of
    /// throwing, so the caller can still attempt the remaining steps and report
    /// every one of them.
    /// </summary>
    private async Task<Exception?> RunPostCommitStepAsync(
        string step,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (attempt == 1 && IsTransientConnectionFailure(exception))
                {
                    logger.LogWarning(
                        exception,
                        "post_step_error: the post-restore {Step} step hit a transient connection failure; " +
                        "dropping the connection pool and retrying once with a fresh connection.",
                        step);
                    NpgsqlConnection.ClearAllPools();
                    continue;
                }

                logger.LogError(
                    exception,
                    "post_step_error: the post-restore {Step} step failed after {AttemptCount} attempt(s).",
                    step,
                    attempt);
                return exception;
            }
        }
    }

    /// <summary>
    /// True for the failures a terminated backend produces: PostgreSQL
    /// <c>57P01</c>/<c>57P02</c>/<c>57P03</c>, the connection-level <c>08…</c>
    /// states, any Npgsql transient error, and a timeout.
    /// </summary>
    private static bool IsTransientConnectionFailure(Exception exception) =>
        exception switch
        {
            PostgresException postgres => postgres.SqlState is
                PostgresErrorCodes.AdminShutdown or
                PostgresErrorCodes.CrashShutdown or
                PostgresErrorCodes.CannotConnectNow or
                PostgresErrorCodes.ConnectionFailure or
                PostgresErrorCodes.ConnectionDoesNotExist or
                PostgresErrorCodes.SqlClientUnableToEstablishSqlConnection,
            NpgsqlException { IsTransient: true } => true,
            TimeoutException => true,
            _ => false,
        };

    /// <summary>
    /// Replaces <c>images/</c> and <c>config/dp-keys/</c> with the staged
    /// copies: the live directories are renamed aside first, then the staged
    /// ones are moved into place. Any failure moves everything back
    /// (docs/M13_PLAN.md §1.9 step 6).
    /// </summary>
    private void SwapLiveDirectories(
        string restoreDirectory,
        string restoreId,
        string stagingDirectory,
        List<string> preservedOriginals)
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
            RollbackSwap(placed, movedLive, preservedOriginals);

            throw new RestoreFailureException(
                ErrorCodes.RestoreFailed,
                "The restored files could not be swapped into place; the previous files were restored.");
        }
    }

    private void RollbackSwap(
        List<(string Live, string Staged)> placed,
        List<(string Old, string Live)> movedLive,
        List<string> preservedOriginals)
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
                preservedOriginals.Add(old);
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

    /// <summary>
    /// Removes a failed restore's working files before the database commit: the
    /// upload and the staging copy are reclaimable, while any <c>old-*</c>
    /// directory (there is none yet) would still be needed for a rollback.
    /// </summary>
    private void RemoveStagingAndUpload(string restoreDirectory, string stagingDirectory)
    {
        TryDeleteFile(Path.Combine(restoreDirectory, UploadFileName));
        RemoveDirectory(stagingDirectory);
        TryDeleteEmptyDirectory(restoreDirectory);
    }

    /// <summary>
    /// Reclaims a post-commit restore's working files. The database already is
    /// the archive's, so the <c>old-*</c> copies are deleted too — except those a
    /// failed swap rollback reported as the only remaining copy of the previous
    /// files (docs/M13_PLAN.md §1.9).
    /// </summary>
    private void ReclaimRestoreFiles(
        string restoreDirectory,
        string stagingDirectory,
        IReadOnlyCollection<string> preservedOriginals)
    {
        TryDeleteFile(Path.Combine(restoreDirectory, UploadFileName));
        RemoveDirectory(stagingDirectory);

        string[] oldDirectories;
        try
        {
            oldDirectories = Directory.Exists(restoreDirectory)
                ? Directory.GetDirectories(restoreDirectory, "old-*")
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "The replaced directories of the restore working directory could not be enumerated.");
            return;
        }

        foreach (var directory in oldDirectories)
        {
            if (preservedOriginals.Contains(directory, StringComparer.Ordinal))
            {
                continue;
            }

            RemoveDirectory(directory);
        }

        TryDeleteEmptyDirectory(restoreDirectory);
    }

    private void RemoveDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "The restore working directory {Directory} could not be removed.",
                directory);
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The restore working file {FilePath} could not be removed.", path);
        }
    }

    private void TryDeleteEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The empty restore directory {Directory} could not be removed.", directory);
        }
    }

    /// <summary>A mapped, already-sanitized restore failure inside the background phase.</summary>
    private sealed class RestoreFailureException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
