using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The atomic restore marker (docs/M13_PLAN.md §1.9 step 8): written after a
/// successful restore and immediately before the process stops, then read and
/// cleared on the next start so the operator gets a recorded outcome instead of
/// a silent restart.
/// </summary>
/// <remarks>
/// The marker lives in <c>{DataDir}/backups</c> next to the archives, is written
/// through a temp file plus rename (a torn file can never be half-read), and
/// contains no key material: only the restore id, the filenames involved, and
/// the app/schema versions.
/// </remarks>
public static class RestoreMarker
{
    /// <summary>Marker file name inside <c>{DataDir}/backups</c>.</summary>
    public const string FileName = "restore.pending";

    private const string TempFileName = FileName + ".tmp";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    /// <summary>Absolute marker path for a configuration.</summary>
    public static string FilePathFor(IConfiguration configuration) =>
        Path.Combine(BackupService.ArchivesDirectory(configuration), FileName);

    public static bool Exists(IConfiguration configuration) =>
        File.Exists(FilePathFor(configuration));

    /// <summary>
    /// Writes the marker atomically. A failure is logged and propagated: a
    /// restore that cannot record its outcome must not silently claim success.
    /// </summary>
    public static void Write(
        IConfiguration configuration,
        RestoreMarkerDocument document,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(document);

        var path = FilePathFor(configuration);
        var tempPath = Path.Combine(
            BackupService.ArchivesDirectory(configuration),
            TempFileName);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(document, Options));
        File.Move(tempPath, path, overwrite: true);

        logger.LogInformation(
            "Wrote the restore marker for restore {RestoreId} at {MarkerPath}.",
            document.RestoreId,
            path);
    }

    /// <summary>Reads the marker, returning null when it is absent or unreadable.</summary>
    public static RestoreMarkerDocument? TryRead(IConfiguration configuration, ILogger logger)
    {
        var path = FilePathFor(configuration);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<RestoreMarkerDocument>(
                File.ReadAllText(path),
                Options);

            if (document is null || string.IsNullOrWhiteSpace(document.RestoreId))
            {
                logger.LogWarning("The restore marker at {MarkerPath} is empty or invalid.", path);
                return null;
            }

            return document;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "The restore marker at {MarkerPath} could not be read.", path);
            return null;
        }
    }

    /// <summary>Removes the marker; returns true when a file was removed.</summary>
    public static bool Delete(IConfiguration configuration, ILogger logger)
    {
        var path = FilePathFor(configuration);
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "The restore marker at {MarkerPath} could not be removed.", path);
            return false;
        }
    }

    /// <summary>
    /// The post-restart check (docs/M13_PLAN.md §1.9 step 8): when the marker
    /// exists, log the recorded outcome, verify the restored database is
    /// reachable, and clear the marker. Idempotent and never fatal: when the
    /// database is still unreachable the marker is kept for the next start.
    /// </summary>
    public static async Task CompleteStartupAsync(
        IConfiguration configuration,
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var document = TryRead(configuration, logger);
        if (document is null)
        {
            return;
        }

        logger.LogInformation(
            "A restore completed before the last restart: restore {RestoreId}, backup {BackupFilename}, " +
            "safety backup {SafetyBackupFilename}, app {AppVersion}, schema {SchemaVersion}, completed {CompletedAt}.",
            document.RestoreId,
            document.BackupFilename,
            document.SafetyBackupFilename,
            document.AppVersion,
            document.SchemaVersion,
            document.CompletedAt.ToString("O", CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(document.PostStepError))
        {
            logger.LogError(
                "Restore {RestoreId} applied the restored database, but a step after the commit failed: " +
                "{PostStepError} The restarted instance has applied migrations against the restored schema.",
                document.RestoreId,
                document.PostStepError);
        }

        if (!await CanReachDatabaseAsync(services, logger, cancellationToken).ConfigureAwait(false))
        {
            logger.LogError(
                "The restored database is not reachable yet; the restore marker is kept so the next start re-checks it.");
            return;
        }

        if (Delete(configuration, logger))
        {
            logger.LogInformation(
                "The restored database is reachable; the restore marker was cleared.");
        }
    }

    private static async Task<bool> CanReachDatabaseAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The restored database connectivity check failed.");
            return false;
        }
    }
}

/// <summary>
/// The content of <see cref="RestoreMarker"/>. Every field is a fact about the
/// completed restore; none is secret.
/// </summary>
public sealed record RestoreMarkerDocument
{
    /// <summary>The restore id returned in the <c>202</c> response.</summary>
    [JsonPropertyName("restore_id")]
    public required string RestoreId { get; init; }

    [JsonPropertyName("backup_filename")]
    public required string BackupFilename { get; init; }

    [JsonPropertyName("safety_backup_filename")]
    public required string SafetyBackupFilename { get; init; }

    [JsonPropertyName("app_version")]
    public required string AppVersion { get; init; }

    /// <summary>The schema version recorded in the restored archive's manifest.</summary>
    [JsonPropertyName("schema_version")]
    public required string SchemaVersion { get; init; }

    /// <summary>
    /// Set when the database restore committed but a later step (the file swap,
    /// retention, or the marker itself) failed. The restore still applied, so the
    /// process must restart; the startup check reports the recorded failure.
    /// </summary>
    [JsonPropertyName("post_step_error")]
    public string? PostStepError { get; init; }

    [JsonPropertyName("completed_at")]
    public required DateTimeOffset CompletedAt { get; init; }
}
