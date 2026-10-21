using System.Diagnostics;
using Npgsql;

namespace FateAndFridge.Api.Services;

/// <summary>
/// The two privileged database steps of a restore (docs/M13_PLAN.md §1.9,
/// §2.1): drop every other session on the target database, then run
/// <c>pg_restore</c> into it. The runner is a seam: the development workstation
/// ships no PostgreSQL client tools, so the restore orchestration is tested with
/// a fake runner and the real binary is only exercised in the runtime image
/// (verified at M13-10).
/// </summary>
public interface IPgRestoreRunner
{
    /// <summary>
    /// Terminates every backend connected to the application database except
    /// this call's own session. The call connects to the <c>postgres</c>
    /// maintenance database so the target can be dropped and recreated by
    /// <c>pg_restore --clean</c>.
    /// </summary>
    Task TerminateConnectionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Restores <paramref name="dumpPath"/> into the application database with
    /// <c>--clean --if-exists --no-owner --no-privileges --single-transaction</c>,
    /// so a failed restore rolls back atomically. Throws when <c>pg_restore</c>
    /// fails, times out, or cannot be started; the message never contains the
    /// password.
    /// </summary>
    Task RestoreAsync(string dumpPath, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IPgRestoreRunner"/>: invokes the shipped
/// <c>pg_restore</c> without a shell, with the password supplied through the
/// <c>PGPASSWORD</c> environment variable, a bounded timeout, and cancellation
/// that kills the process tree (docs/SECURITY.md §13, docs/M13_PLAN.md §4). The
/// connection-termination query runs over Npgsql, also without a shell.
/// </summary>
public sealed class PgRestoreRunner : IPgRestoreRunner
{
    /// <summary>The executable resolved through <c>PATH</c> in the runtime image.</summary>
    public const string DefaultExecutablePath = "pg_restore";

    /// <summary>
    /// Upper bound for one restore. A restore also recreates indexes, so it is
    /// allowed twice the dump bound.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(2);

    /// <summary>The maintenance database used to terminate the target's sessions.</summary>
    public const string MaintenanceDatabase = "postgres";

    private readonly string _executablePath;
    private readonly TimeSpan _timeout;
    private readonly string _connectionString;
    private readonly ILogger<PgRestoreRunner> _logger;

    public PgRestoreRunner(
        IConfiguration configuration,
        ILogger<PgRestoreRunner> logger)
        : this(
            configuration.GetConnectionString("Default")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Default is required to run pg_restore."),
            DefaultExecutablePath,
            DefaultTimeout,
            logger)
    {
    }

    public PgRestoreRunner(
        string connectionString,
        string executablePath,
        TimeSpan timeout,
        ILogger<PgRestoreRunner> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _connectionString = connectionString;
        _executablePath = executablePath;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task TerminateConnectionsAsync(CancellationToken cancellationToken)
    {
        var targetDatabase = new NpgsqlConnectionStringBuilder(_connectionString).Database;
        if (string.IsNullOrWhiteSpace(targetDatabase))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Default has no Database; the restore target is unknown.");
        }

        var maintenanceConnectionString = BuildMaintenanceConnectionString(_connectionString);

        try
        {
            await using var connection = new NpgsqlConnection(maintenanceConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = new NpgsqlCommand(
                """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @database
                  AND pid <> pg_backend_pid()
                """,
                connection);
            command.CommandTimeout = (int)TimeSpan.FromMinutes(1).TotalSeconds;
            command.Parameters.AddWithValue("database", targetDatabase);

            var terminated = await command
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Terminated {TerminatedCount} session(s) on the restore target database.",
                terminated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // Never echo the connection string: it can carry the password.
            throw new InvalidOperationException(
                "Existing database connections could not be terminated for the restore: " +
                exception.Message,
                exception);
        }
    }

    public async Task RestoreAsync(string dumpPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);

        var startInfo = BuildStartInfo(_executablePath, _connectionString, dumpPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("pg_restore could not be started.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "pg_restore is not available (the PostgreSQL client tools are missing from the image).",
                exception);
        }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(
                    $"pg_restore did not finish within {_timeout.TotalMinutes:0} minutes.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"pg_restore failed with exit code {process.ExitCode}: {Summarize(stderr)}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
            }
        }
    }

    /// <summary>
    /// Builds the process start information for one restore. Arguments go
    /// through <see cref="ProcessStartInfo.ArgumentList"/> (never a shell
    /// string) and the password is only ever an environment variable, so it
    /// cannot appear in the process arguments, an error message, or a log line.
    /// <c>--single-transaction</c> makes a failed restore roll back, which is
    /// what keeps the original data intact.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string connectionString,
        string dumpPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(dumpPath);

        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(settings.Database))
        {
            throw new ArgumentException(
                "The connection string must name the database to restore into.",
                nameof(connectionString));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--clean");
        startInfo.ArgumentList.Add("--if-exists");
        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        startInfo.ArgumentList.Add("--single-transaction");
        // Never prompt: a credential prompt would hang the request.
        startInfo.ArgumentList.Add("--no-password");
        startInfo.ArgumentList.Add($"--dbname={settings.Database}");

        if (!string.IsNullOrWhiteSpace(settings.Host))
        {
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(settings.Host);
        }

        if (settings.Port > 0)
        {
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(
                settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            startInfo.ArgumentList.Add("--username");
            startInfo.ArgumentList.Add(settings.Username);
        }

        startInfo.ArgumentList.Add(dumpPath);

        if (!string.IsNullOrEmpty(settings.Password))
        {
            startInfo.Environment["PGPASSWORD"] = settings.Password;
        }

        return startInfo;
    }

    /// <summary>
    /// The connection string for the maintenance database: same host, port,
    /// credentials, and TLS options, but <c>Database=postgres</c> and no pooling
    /// (a single short-lived session).
    /// </summary>
    internal static string BuildMaintenanceConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Database = MaintenanceDatabase,
            Pooling = false,
        };

        return settings.ConnectionString;
    }

    private static string Summarize(string stderr)
    {
        var text = stderr.Trim();
        if (text.Length == 0)
        {
            return "no diagnostic output";
        }

        const int maximum = 500;
        return text.Length <= maximum ? text : text[..maximum];
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
