using System.Diagnostics;
using System.Globalization;
using FateAndFridge.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The PostgreSQL server version as reported by the live connection
/// </summary>
public sealed record PgServerVersion(int? Major, string? Version);

/// <summary>
/// Result of the startup backup-tooling probe: the installed <c>pg_dump</c>
/// client and the PostgreSQL server it would talk to
/// </summary>
public sealed record PgToolingStatus(
    string? ClientVersion,
    int? ClientMajor,
    string? ServerVersion,
    int? ServerMajor)
{
    /// <summary>
    /// A custom-format dump is readable by a client whose major is at least the
    /// server's; an older client cannot dump a newer server
    /// An unknown version never counts as supported.
    /// </summary>
    public bool SupportsBackup =>
        ClientMajor is { } client && ServerMajor is { } server && client >= server;
}

/// <summary>
/// Parses the version strings used by the backup tooling probe. Parsing is
/// deliberately narrow: an unrecognized string is reported as unknown instead
/// of being guessed, because guessing could turn an incompatible client into a
/// silently broken backup.
/// </summary>
public static class PgVersionParser
{
    private const string ClientVersionPrefix = "pg_dump (PostgreSQL) ";

    /// <summary>Parses the major from <c>pg_dump --version</c> output.</summary>
    public static bool TryParseClientMajor(string? versionOutput, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(versionOutput))
        {
            return false;
        }

        var text = versionOutput.Trim();
        if (!text.StartsWith(ClientVersionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryParseLeadingMajor(text.AsSpan(ClientVersionPrefix.Length), out major);
    }

    /// <summary>Parses the major from PostgreSQL's numeric <c>server_version_num</c>.</summary>
    public static bool TryParseServerVersionNumber(string? versionNumber, out int major)
    {
        major = 0;
        if (!int.TryParse(versionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number <= 0)
        {
            return false;
        }

        // 170004 → 17, and 90624 → 9 for pre-10 releases.
        major = number / 10000;
        return major > 0;
    }

    /// <summary>Parses the major from the textual <c>server_version</c> setting.</summary>
    public static bool TryParseServerVersion(string? serverVersion, out int major)
    {
        major = 0;
        return !string.IsNullOrWhiteSpace(serverVersion)
            && TryParseLeadingMajor(serverVersion.AsSpan().TrimStart(), out major);
    }

    private static bool TryParseLeadingMajor(ReadOnlySpan<char> value, out int major)
    {
        major = 0;

        var digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }

        return digits > 0
            && int.TryParse(value[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out major)
            && major > 0;
    }
}

/// <summary>Reads the installed <c>pg_dump</c> version output.</summary>
public interface IPgDumpVersionReader
{
    /// <summary>Returns the raw output, or null when the client is unavailable.</summary>
    Task<string?> ReadVersionOutputAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Runs <c>pg_dump --version</c> without a shell and with a bounded timeout
/// A missing binary, a non-zero exit, or a timeout is
/// reported as "unknown" rather than failing application startup.
/// </summary>
public sealed class PgDumpVersionReader : IPgDumpVersionReader
{
    /// <summary>The executable resolved through <c>PATH</c> in the runtime image.</summary>
    public const string DefaultExecutablePath = "pg_dump";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly string _executablePath;
    private readonly TimeSpan _timeout;

    public PgDumpVersionReader()
        : this(DefaultExecutablePath, DefaultTimeout)
    {
    }

    public PgDumpVersionReader(string executablePath, TimeSpan timeout)
    {
        _executablePath = executablePath;
        _timeout = timeout;
    }

    public async Task<string?> ReadVersionOutputAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Arguments go through ArgumentList, never a shell string, so no
        // caller-supplied text can be interpreted as a command.
        startInfo.ArgumentList.Add("--version");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A missing or non-executable pg_dump is an expected deployment
            // state; the probe reports it instead of crashing startup.
            return null;
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return null;
            }

            return await stdoutTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The probe timed out; the caller's own cancellation still flows.
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process exited between the check and the kill.
        }
    }
}

/// <summary>Reads the PostgreSQL server version through the live connection.</summary>
public interface IPgServerVersionReader
{
    Task<PgServerVersion> ReadVersionAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Queries <c>server_version_num</c> and <c>server_version</c> over Npgsql
/// Failures are handled by the caller so startup is
/// never blocked by an unavailable database.
/// </summary>
public sealed class NpgsqlServerVersionReader(AppDbContext dbContext) : IPgServerVersionReader
{
    public async Task<PgServerVersion> ReadVersionAsync(CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT current_setting('server_version_num'), current_setting('server_version')";

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new PgServerVersion(null, null);
            }

            var versionNumber = reader.IsDBNull(0) ? null : reader.GetString(0);
            var version = reader.IsDBNull(1) ? null : reader.GetString(1);
            var major = PgVersionParser.TryParseServerVersionNumber(versionNumber, out var parsed)
                ? parsed
                : (int?)null;

            return new PgServerVersion(major, version);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Records whether the shipped <c>pg_dump</c> can dump the running PostgreSQL
/// server. The probe never throws for a missing client
/// or an unreachable server; it reports unknown versions and logs a warning so
/// Can refuse to create a backup with a clear reason.
/// </summary>
public sealed class PgToolingInfo(
    IPgDumpVersionReader dumpVersionReader,
    IPgServerVersionReader serverVersionReader,
    ILogger<PgToolingInfo> logger)
{
    public async Task<PgToolingStatus> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var clientVersion = await TryReadClientVersionAsync(cancellationToken).ConfigureAwait(false);
        int? clientMajor = clientVersion is not null
            && PgVersionParser.TryParseClientMajor(clientVersion, out var parsedClientMajor)
                ? parsedClientMajor
                : null;

        if (clientMajor is null)
        {
            logger.LogWarning(
                "pg_dump is unavailable or its version could not be parsed; backups cannot be created.");
        }

        var server = await TryReadServerVersionAsync(cancellationToken).ConfigureAwait(false);
        var status = new PgToolingStatus(clientVersion, clientMajor, server.Version, server.Major);

        if (!status.SupportsBackup)
        {
            logger.LogWarning(
                "PostgreSQL backup tooling is not compatible: pg_dump major {ClientMajor}, " +
                "server major {ServerMajor}; a backup requires a client at least as new as the server.",
                status.ClientMajor,
                status.ServerMajor);
        }

        return status;
    }

    private async Task<string?> TryReadClientVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await dumpVersionReader
                .ReadVersionOutputAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The pg_dump version probe failed.");
            return null;
        }
    }

    private async Task<PgServerVersion> TryReadServerVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await serverVersionReader
                .ReadVersionAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The PostgreSQL server version probe failed.");
            return new PgServerVersion(null, null);
        }
    }
}
