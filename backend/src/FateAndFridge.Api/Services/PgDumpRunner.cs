using System.Diagnostics;
using Npgsql;

namespace FateAndFridge.Api.Services;

/// <summary>
/// Runs one <c>pg_dump --format=custom</c> against the application database
/// (docs/M13_PLAN.md §1.3, §2.1). The runner is a seam: the development
/// workstation has no <c>pg_dump</c>, so the backup orchestration is tested with
/// a fake runner and the real binary is only exercised in the runtime image.
/// </summary>
public interface IPgDumpRunner
{
    /// <summary>
    /// Writes a custom-format dump of the application database to
    /// <paramref name="outputPath"/>. Throws when pg_dump fails, times out, or
    /// cannot be started; the exception message never contains the password.
    /// </summary>
    Task RunAsync(string outputPath, CancellationToken cancellationToken);
}

/// <summary>
/// Production <see cref="IPgDumpRunner"/>: invokes the shipped
/// <c>pg_dump</c> without a shell, with the password supplied through the
/// <c>PGPASSWORD</c> environment variable, a bounded timeout, and cancellation
/// that kills the process tree (docs/SECURITY.md §13, docs/M13_PLAN.md §4).
/// </summary>
public sealed class PgDumpRunner : IPgDumpRunner
{
    /// <summary>The executable resolved through <c>PATH</c> in the runtime image.</summary>
    public const string DefaultExecutablePath = "pg_dump";

    /// <summary>
    /// Upper bound for one dump. Large households take minutes, not hours; the
    /// bound keeps a hung client from pinning the request forever.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromHours(1);

    private readonly string _executablePath;
    private readonly TimeSpan _timeout;
    private readonly string _connectionString;
    private readonly ILogger<PgDumpRunner> _logger;

    public PgDumpRunner(
        IConfiguration configuration,
        ILogger<PgDumpRunner> logger)
        : this(
            configuration.GetConnectionString("Default")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:Default is required to run pg_dump."),
            DefaultExecutablePath,
            DefaultTimeout,
            logger)
    {
    }

    public PgDumpRunner(
        string connectionString,
        string executablePath,
        TimeSpan timeout,
        ILogger<PgDumpRunner> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        _connectionString = connectionString;
        _executablePath = executablePath;
        _timeout = timeout;
        _logger = logger;
    }

    public async Task RunAsync(string outputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var startInfo = BuildStartInfo(_executablePath, _connectionString, outputPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("pg_dump could not be started.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "pg_dump is not available (the PostgreSQL client tools are missing from the image).",
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
                    $"pg_dump did not finish within {_timeout.TotalMinutes:0} minutes.");
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
                    $"pg_dump failed with exit code {process.ExitCode}: {Summarize(stderr)}");
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
    /// Builds the process start information for one dump. Arguments go through
    /// <see cref="ProcessStartInfo.ArgumentList"/> (never a shell string) and
    /// the password is only ever an environment variable, so it cannot appear in
    /// the process arguments, an error message, or a log line.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string executablePath,
        string connectionString,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var settings = new NpgsqlConnectionStringBuilder(connectionString);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("--format=custom");
        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        // Never prompt: a credential prompt would hang the request.
        startInfo.ArgumentList.Add("--no-password");
        startInfo.ArgumentList.Add("--file");
        startInfo.ArgumentList.Add(outputPath);

        if (!string.IsNullOrWhiteSpace(settings.Host))
        {
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(settings.Host);
        }

        if (settings.Port > 0)
        {
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            startInfo.ArgumentList.Add("--username");
            startInfo.ArgumentList.Add(settings.Username);
        }

        if (!string.IsNullOrWhiteSpace(settings.Database))
        {
            startInfo.ArgumentList.Add("--dbname");
            startInfo.ArgumentList.Add(settings.Database);
        }

        if (!string.IsNullOrEmpty(settings.Password))
        {
            startInfo.Environment["PGPASSWORD"] = settings.Password;
        }

        return startInfo;
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
