using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using FateAndFridge.Api.Backup;
using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Errors;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Serializes backup creation inside this process so two requests can never run
/// two <c>pg_dump</c> processes (docs/M13_PLAN.md §4: "concurrent backup requests
/// are serialized"). A second attempt is told <c>BACKUP_IN_PROGRESS</c> instead
/// of waiting; the endpoints answer immediately and the UI re-lists.
/// </summary>
public sealed class BackupOperationGate
{
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    /// <summary>Enters when no backup is running; false when one already is.</summary>
    public bool TryEnter() => _gate.Wait(0);

    public void Exit() => _gate.Release();
}

/// <summary>Result of a backup creation attempt (docs/API.md §18).</summary>
public abstract record BackupCreateOutcome
{
    private BackupCreateOutcome()
    {
    }

    public sealed record Success(BackupMetadata Backup) : BackupCreateOutcome;

    public sealed record Failure(int StatusCode, string Code, string Message) : BackupCreateOutcome;
}

/// <summary>Result of opening one archive for download.</summary>
public abstract record BackupDownloadOutcome
{
    private BackupDownloadOutcome()
    {
    }

    public sealed record Success(string Path, string Filename) : BackupDownloadOutcome;

    public sealed record Failure(int StatusCode, string Code, string Message) : BackupDownloadOutcome;
}

/// <summary>Result of deleting one archive.</summary>
public abstract record BackupDeleteOutcome
{
    private BackupDeleteOutcome()
    {
    }

    public sealed record Success(BackupMetadata Backup) : BackupDeleteOutcome;

    public sealed record Failure(int StatusCode, string Code, string Message) : BackupDeleteOutcome;
}

/// <summary>
/// The M13-03 backup lifecycle (docs/M13_PLAN.md §1.4–§1.8, §2.1, §4;
/// docs/SECURITY.md §11, §12): <c>pg_dump</c> capture, encrypted archive
/// creation, the <c>backup_metadata</c> row, list/download/delete, and the
/// last-five safety-backup retention.
/// </summary>
/// <remarks>
/// <para>
/// A create inserts a <see cref="BackupStatus.Pending"/> row first, dumps into
/// <c>{DataDir}/backups/.tmp</c>, writes the encrypted archive to a sibling
/// temp file, renames it into place, and only then marks the row
/// <see cref="BackupStatus.Completed"/> with the ciphertext checksum and size.
/// Any failure marks the row <see cref="BackupStatus.Failed"/> with the error,
/// removes partial files, and writes no success audit row.
/// </para>
/// <para>
/// The recovery key is read through <see cref="RecoveryKeyStore.TryReadAsync"/>
/// and never created here, never stored in the database, and never logged. The
/// archive never contains <c>recovery.key</c> itself; only
/// <c>database.dump</c>, <c>images/…</c>, <c>config/dp-keys/…</c>, and the
/// generated <c>manifest.json</c>.
/// </para>
/// </remarks>
public sealed class BackupService(
    AppDbContext dbContext,
    IPgDumpRunner dumpRunner,
    PgToolingInfo toolingInfo,
    RecoveryKeyStore recoveryKeyStore,
    IAuditService auditService,
    BackupOperationGate operationGate,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<BackupService> logger)
{
    /// <summary>How many safety archives survive a retention prune.</summary>
    public const int SafetyBackupRetention = 5;

    /// <summary>Subdirectory of the data root that holds archives (docs/M13_PLAN.md §1.6).</summary>
    public const string ArchivesDirectoryName = "backups";

    private const int MaximumErrorMessageLength = 1000;

    private const int MaximumFilenameAttempts = 100;

    /// <summary>Absolute <c>{DataDir}/backups</c> directory.</summary>
    public static string ArchivesDirectory(IConfiguration configuration) =>
        Path.Combine(AppPaths.DataRoot(configuration), ArchivesDirectoryName);

    /// <summary>Absolute <c>{DataDir}/backups/safety</c> directory.</summary>
    public static string SafetyArchivesDirectory(IConfiguration configuration) =>
        Path.Combine(ArchivesDirectory(configuration), BackupNaming.SafetyDirectoryName);

    /// <summary>
    /// Absolute path of a row's archive. Safety archives live in <c>safety/</c>
    /// and manual archives directly in <c>backups/</c>; the filename is always
    /// server-generated, so it cannot traverse outside the backups directory.
    /// </summary>
    public static string ResolveArchivePath(IConfiguration configuration, BackupMetadata backup) =>
        Path.Combine(
            backup.Kind == BackupKind.Safety
                ? SafetyArchivesDirectory(configuration)
                : ArchivesDirectory(configuration),
            backup.Filename);

    /// <summary>Archives, newest first (docs/API.md §18).</summary>
    public async Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken cancellationToken)
    {
        return await dbContext.BackupMetadata
            .AsNoTracking()
            .OrderByDescending(backup => backup.CreatedAt)
            .ThenByDescending(backup => backup.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates one manual backup and returns the completed (or failed) row.
    /// Refusals before any work happens: an incompatible <c>pg_dump</c>
    /// (<c>BACKUP_CLIENT_TOO_OLD</c>), a missing or unusable recovery key
    /// (<c>BACKUP_KEY_MISSING</c>), and a create already running
    /// (<c>BACKUP_IN_PROGRESS</c>).
    /// </summary>
    public Task<BackupCreateOutcome> CreateAsync(
        string reason,
        long? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(BackupKind.Manual, reason, actorUserId, ipAddress, acquireGate: true, cancellationToken);

    /// <summary>
    /// Creates the mandatory pre-restore safety backup in
    /// <c>{DataDir}/backups/safety</c> (docs/M13_PLAN.md §1.6, §1.9 step 2).
    /// The restore already holds <see cref="BackupOperationGate"/>, so this
    /// entry point never takes it: a re-entrant acquire would deadlock the
    /// restore against itself. Apart from the kind and the destination
    /// directory it behaves exactly like <see cref="CreateAsync"/>.
    /// </summary>
    public Task<BackupCreateOutcome> CreateSafetyBackupAsync(
        string reason,
        long? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(BackupKind.Safety, reason, actorUserId, ipAddress, acquireGate: false, cancellationToken);

    private async Task<BackupCreateOutcome> CreateCoreAsync(
        BackupKind kind,
        string reason,
        long? actorUserId,
        string? ipAddress,
        bool acquireGate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (acquireGate && !operationGate.TryEnter())
        {
            return new BackupCreateOutcome.Failure(
                StatusCodes.Status409Conflict,
                ErrorCodes.BackupInProgress,
                "A backup is already being created. Try again when it finishes.");
        }

        try
        {
            var tooling = await toolingInfo.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (!tooling.SupportsBackup)
            {
                return new BackupCreateOutcome.Failure(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.BackupClientTooOld,
                    "Backups are unavailable: the installed pg_dump client is older than the " +
                    "PostgreSQL server (or its version could not be determined).");
            }

            byte[] recoveryKey;
            try
            {
                var keyText = await recoveryKeyStore.TryReadAsync(cancellationToken).ConfigureAwait(false);
                if (keyText is null)
                {
                    return KeyMissing();
                }

                recoveryKey = Base64Url.DecodeFromChars(keyText);
                if (recoveryKey.Length != BackupArchiveFormat.RecoveryKeySize)
                {
                    CryptographicOperations.ZeroMemory(recoveryKey);
                    return KeyMissing();
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException)
            {
                // An unusable key file is treated like a missing one: no archive
                // may be produced that the household cannot decrypt.
                logger.LogWarning(
                    exception,
                    "The recovery key file is unusable; the backup was refused.");
                return KeyMissing();
            }

            var createdAt = timeProvider.GetUtcNow();
            var appVersion = AppInfo.Version;
            var schemaVersion = await ReadSchemaVersionAsync(cancellationToken).ConfigureAwait(false);

            var archivesDirectory = ArchivesDirectory(configuration);
            var tempDirectory = Path.Combine(archivesDirectory, BackupNaming.TempDirectoryName);
            var safetyDirectory = SafetyArchivesDirectory(configuration);
            var targetDirectory = kind == BackupKind.Safety ? safetyDirectory : archivesDirectory;
            Directory.CreateDirectory(archivesDirectory);
            Directory.CreateDirectory(tempDirectory);
            Directory.CreateDirectory(safetyDirectory);

            var filename = await ReserveFilenameAsync(targetDirectory, appVersion, createdAt, cancellationToken)
                .ConfigureAwait(false);
            var backup = new BackupMetadata
            {
                Filename = filename,
                AppVersion = appVersion,
                SchemaVersion = schemaVersion,
                CreatedAt = createdAt,
                CreatedByUserId = actorUserId,
                Checksum = string.Empty,
                SizeBytes = 0,
                Kind = kind,
                Status = BackupStatus.Pending,
            };

            dbContext.BackupMetadata.Add(backup);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var dumpPath = Path.Combine(
                tempDirectory,
                $"{filename}.{Guid.NewGuid():N}.dump");
            var archiveTempPath = Path.Combine(targetDirectory, $"{filename}.tmp");
            var archivePath = Path.Combine(targetDirectory, filename);

            try
            {
                await dumpRunner.RunAsync(dumpPath, cancellationToken).ConfigureAwait(false);

                var dump = new FileInfo(dumpPath);
                if (!dump.Exists || dump.Length == 0)
                {
                    throw new InvalidOperationException("pg_dump produced no output.");
                }

                var result = await WriteArchiveAsync(
                        archiveTempPath,
                        dumpPath,
                        recoveryKey,
                        appVersion,
                        schemaVersion,
                        createdAt,
                        cancellationToken)
                    .ConfigureAwait(false);

                File.Move(archiveTempPath, archivePath);

                backup.Checksum = result.Sha256;
                backup.SizeBytes = result.SizeBytes;
                backup.Status = BackupStatus.Completed;
                backup.Error = null;

                await auditService.WriteAsync(
                        new AuditEventEntry(
                            AuditEventTypes.BackupCreated,
                            AuditEntityTypes.Backup,
                            backup.Id.ToString(CultureInfo.InvariantCulture),
                            Reason: reason,
                            IpAddress: ipAddress,
                            ActorUserId: actorUserId,
                            Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["filename"] = backup.Filename,
                                ["kind"] = kind == BackupKind.Safety ? "safety" : "manual",
                                ["size_bytes"] = backup.SizeBytes,
                                ["checksum"] = backup.Checksum,
                            }),
                        cancellationToken)
                    .ConfigureAwait(false);

                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return new BackupCreateOutcome.Success(backup);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                RemovePartialFiles(dumpPath, archiveTempPath, archivePath);
                await MarkFailedAsync(backup, "The backup was cancelled.", CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                RemovePartialFiles(dumpPath, archiveTempPath, archivePath);
                await MarkFailedAsync(backup, Describe(exception), CancellationToken.None)
                    .ConfigureAwait(false);

                return new BackupCreateOutcome.Failure(
                    StatusCodes.Status500InternalServerError,
                    ErrorCodes.BackupFailed,
                    "The backup could not be created. See the backup status for the recorded error.");
            }
            finally
            {
                TryDelete(dumpPath);
                TryDelete(archiveTempPath);
                CryptographicOperations.ZeroMemory(recoveryKey);
            }
        }
        finally
        {
            if (acquireGate)
            {
                operationGate.Exit();
            }
        }
    }

    /// <summary>
    /// Opens an archive for download. A missing/deleted row is
    /// <c>BACKUP_NOT_FOUND</c>; a row whose file has disappeared is
    /// <c>BACKUP_FILE_MISSING</c> (both 404, docs/API.md §18).
    /// </summary>
    public async Task<BackupDownloadOutcome> OpenDownloadAsync(
        long id,
        CancellationToken cancellationToken)
    {
        var backup = await dbContext.BackupMetadata
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (backup is null || backup.Status == BackupStatus.Deleted)
        {
            return new BackupDownloadOutcome.Failure(
                StatusCodes.Status404NotFound,
                ErrorCodes.BackupNotFound,
                "The backup does not exist.");
        }

        var path = ResolveArchivePath(configuration, backup);
        if (!File.Exists(path))
        {
            return new BackupDownloadOutcome.Failure(
                StatusCodes.Status404NotFound,
                ErrorCodes.BackupFileMissing,
                "The backup file is missing from the backups directory.");
        }

        return new BackupDownloadOutcome.Success(path, backup.Filename);
    }

    /// <summary>
    /// Deletes one archive: removes the file and marks the row
    /// <see cref="BackupStatus.Deleted"/> with a <c>backup.deleted</c> audit row
    /// (docs/M13_PLAN.md §1.8). The caller must have validated the reason and
    /// the typed <c>DELETE</c> confirmation with
    /// <see cref="DangerousActionGuard"/>. A still-pending row is refused so a
    /// running create's file is never deleted underneath it.
    /// </summary>
    public async Task<BackupDeleteOutcome> DeleteAsync(
        long id,
        string reason,
        long? actorUserId,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var backup = await dbContext.BackupMetadata
            .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (backup is null || backup.Status == BackupStatus.Deleted)
        {
            return new BackupDeleteOutcome.Failure(
                StatusCodes.Status404NotFound,
                ErrorCodes.BackupNotFound,
                "The backup does not exist.");
        }

        if (backup.Status == BackupStatus.Pending)
        {
            return new BackupDeleteOutcome.Failure(
                StatusCodes.Status409Conflict,
                ErrorCodes.BackupInProgress,
                "A backup that is still being created cannot be deleted.");
        }

        var path = ResolveArchivePath(configuration, backup);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "The backup file {BackupFilename} could not be deleted.", backup.Filename);
            return new BackupDeleteOutcome.Failure(
                StatusCodes.Status500InternalServerError,
                ErrorCodes.BackupFailed,
                "The backup file could not be deleted.");
        }

        backup.Status = BackupStatus.Deleted;

        await auditService.WriteAsync(
                new AuditEventEntry(
                    AuditEventTypes.BackupDeleted,
                    AuditEntityTypes.Backup,
                    backup.Id.ToString(CultureInfo.InvariantCulture),
                    Reason: reason,
                    IpAddress: ipAddress,
                    ActorUserId: actorUserId,
                    Metadata: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["filename"] = backup.Filename,
                        ["kind"] = backup.Kind == BackupKind.Safety ? "safety" : "manual",
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new BackupDeleteOutcome.Success(backup);
    }

    /// <summary>
    /// Retention for pre-restore safety archives
    /// (docs/M13_PLAN.md §1.8): keeps the newest <paramref name="keep"/> safety
    /// rows and their files, marks older ones <see cref="BackupStatus.Deleted"/>,
    /// and never touches a <see cref="BackupKind.Manual"/> archive. A pending
    /// safety archive (a restore that is still running) is never pruned.
    /// </summary>
    public async Task<int> PruneSafetyBackupsAsync(
        int keep = SafetyBackupRetention,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        var candidates = await dbContext.BackupMetadata
            .Where(backup => backup.Kind == BackupKind.Safety
                && backup.Status != BackupStatus.Pending
                && backup.Status != BackupStatus.Deleted)
            .OrderByDescending(backup => backup.CreatedAt)
            .ThenByDescending(backup => backup.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var expired = candidates.Skip(keep).ToList();
        foreach (var backup in expired)
        {
            TryDelete(ResolveArchivePath(configuration, backup));
            backup.Status = BackupStatus.Deleted;
        }

        if (expired.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Pruned {PrunedCount} safety backup(s), keeping the newest {KeepCount}.",
                expired.Count,
                keep);
        }

        return expired.Count;
    }

    private static BackupCreateOutcome KeyMissing() =>
        new BackupCreateOutcome.Failure(
            StatusCodes.Status409Conflict,
            ErrorCodes.BackupKeyMissing,
            "Backups are unavailable: the recovery key file is missing or unusable. " +
            "Complete setup to create the recovery key.");

    /// <summary>
    /// The applied migration id at archive creation (docs/DATABASE.md §12).
    /// A database with no applied migration is recorded as <c>unapplied</c>
    /// rather than guessing a version.
    /// </summary>
    private async Task<string> ReadSchemaVersionAsync(CancellationToken cancellationToken)
    {
        var applied = await dbContext.Database
            .GetAppliedMigrationsAsync(cancellationToken)
            .ConfigureAwait(false);

        var latest = applied.LastOrDefault();
        return string.IsNullOrWhiteSpace(latest) ? "unapplied" : latest;
    }

    private async Task<string> ReserveFilenameAsync(
        string archivesDirectory,
        string appVersion,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var filename = BackupNaming.ForManualBackup(appVersion, createdAt);
        for (var attempt = 1; attempt <= MaximumFilenameAttempts; attempt++)
        {
            var candidate = attempt == 1 ? filename : BackupNaming.WithSuffix(filename, attempt);

            var exists = await dbContext.BackupMetadata
                .AnyAsync(backup => backup.Filename == candidate, cancellationToken)
                .ConfigureAwait(false);

            if (!exists && !File.Exists(Path.Combine(archivesDirectory, candidate)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "A unique backup filename could not be reserved in the current second.");
    }

    private async Task<BackupArchiveResult> WriteArchiveAsync(
        string archiveTempPath,
        string dumpPath,
        byte[] recoveryKey,
        string appVersion,
        string schemaVersion,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var manifest = new BackupManifest
        {
            AppVersion = appVersion,
            SchemaVersion = schemaVersion,
            CreatedAt = createdAt,
        };

        await using var output = new FileStream(
            archiveTempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var writer = await BackupArchiveWriter
            .WriteAsync(output, recoveryKey, manifest, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await AddFileEntryAsync(writer, "database.dump", dumpPath, cancellationToken)
                .ConfigureAwait(false);

            await AddDirectoryEntriesAsync(
                    writer,
                    Path.Combine(AppPaths.DataRoot(configuration), "images"),
                    "images",
                    cancellationToken)
                .ConfigureAwait(false);

            await AddDirectoryEntriesAsync(
                    writer,
                    AppPaths.DataProtectionKeysRoot(configuration),
                    "config/dp-keys",
                    cancellationToken)
                .ConfigureAwait(false);

            return await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AddFileEntryAsync(
        BackupArchiveWriter writer,
        string entryName,
        string path,
        CancellationToken cancellationToken)
    {
        await using var content = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await writer.AddEntryAsync(entryName, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds every regular file below <paramref name="root"/> under
    /// <paramref name="entryPrefix"/>. Symbolic links and other reparse points
    /// are skipped so the archive can never pull in a file from outside
    /// <c>/data/{images,config/dp-keys}</c> (docs/M13_PLAN.md §1.5,
    /// docs/SECURITY.md §12). Entries are added in ordinal path order so the
    /// same data always produces the same archive ordering.
    /// </summary>
    private static async Task AddDirectoryEntriesAsync(
        BackupArchiveWriter writer,
        string root,
        string entryPrefix,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }

                files.Add(entry);
            }
        }

        files.Sort(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal))
            {
                // Defensive: enumeration starts at the root, so this cannot
                // happen, but an escaping name must never reach the archive.
                continue;
            }

            await AddFileEntryAsync(writer, $"{entryPrefix}/{relative}", file, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task MarkFailedAsync(
        BackupMetadata backup,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            backup.Status = BackupStatus.Failed;
            backup.Error = Bound(error);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The failure is already being reported; a failed status write must
            // not mask it. Log without any backup content.
            logger.LogError(
                exception,
                "The failed state of backup {BackupFilename} could not be recorded.",
                backup.Filename);
        }
    }

    private void RemovePartialFiles(params string[] paths)
    {
        foreach (var path in paths)
        {
            TryDelete(path);
        }
    }

    private void TryDelete(string path)
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
            logger.LogWarning(exception, "A partial backup file could not be removed.");
        }
    }

    private string Describe(Exception exception)
    {
        // Never includes the recovery key or dump contents: pg_dump diagnostics
        // never echo PGPASSWORD, and the archive writer's messages name entries only.
        logger.LogError(exception, "Backup creation failed.");
        return Bound(exception.Message);
    }

    private static string Bound(string message) =>
        message.Length <= MaximumErrorMessageLength
            ? message
            : message[..MaximumErrorMessageLength];
}
