using FateAndFridge.Api.Services;
using Npgsql;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Deterministic <see cref="IPgRestoreRunner"/> for the M13-04 tests. The
/// development workstation ships no PostgreSQL client tools, so the restore
/// orchestration is proven with this fake; the real binary is covered by the
/// conditional integration test and the M13-10 image gate.
/// </summary>
public sealed class RecordingPgRestoreRunner : IPgRestoreRunner
{
    private readonly Func<string, CancellationToken, Task> _behaviour;

    private RecordingPgRestoreRunner(Func<string, CancellationToken, Task> behaviour)
    {
        _behaviour = behaviour;
    }

    /// <summary>How many times the pre-restore connection termination ran.</summary>
    public int TerminateCalls { get; private set; }

    /// <summary>Every dump path the runner was asked to restore, in call order.</summary>
    public List<string> RestoredPaths { get; } = [];

    /// <summary>Runs inside the restore call, for asserting the observed state.</summary>
    public Action<string>? OnRestore { get; set; }

    public static RecordingPgRestoreRunner Succeeding() => new((_, _) => Task.CompletedTask);

    /// <summary>Fails the way a <c>pg_restore</c> error exit would.</summary>
    public static RecordingPgRestoreRunner Failing(string message) =>
        new((_, _) => throw new InvalidOperationException(message));

    public Task TerminateConnectionsAsync(CancellationToken cancellationToken)
    {
        TerminateCalls++;
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(string dumpPath, CancellationToken cancellationToken)
    {
        RestoredPaths.Add(dumpPath);
        OnRestore?.Invoke(dumpPath);
        await _behaviour(dumpPath, cancellationToken);
    }
}

/// <summary>
/// A runner that blocks inside the restore until the test releases it, so a
/// concurrent restore or backup can be attempted while one is provably in
/// flight and maintenance mode can be observed through the HTTP pipeline.
/// </summary>
public sealed class GatedPgRestoreRunner : IPgRestoreRunner
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int TerminateCalls { get; private set; }

    public List<string> RestoredPaths { get; } = [];

    public Task TerminateConnectionsAsync(CancellationToken cancellationToken)
    {
        TerminateCalls++;
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(string dumpPath, CancellationToken cancellationToken)
    {
        RestoredPaths.Add(dumpPath);
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// Reproduces the live 57P01 failure: it holds an application-pooled connection
/// across a <c>pg_terminate_backend</c> sweep and returns without clearing the
/// pool, so the next pooled use is a dead connection. The restore service must
/// retry it with a fresh connection and still write the <c>backup.restored</c>
/// audit row.
/// </summary>
public sealed class TerminatingPgRestoreRunner(string connectionString) : IPgRestoreRunner
{
    public int TerminateCalls { get; private set; }

    public List<string> RestoredPaths { get; } = [];

    public Task TerminateConnectionsAsync(CancellationToken cancellationToken)
    {
        TerminateCalls++;
        return Task.CompletedTask;
    }

    public async Task RestoreAsync(string dumpPath, CancellationToken cancellationToken)
    {
        RestoredPaths.Add(dumpPath);

        // Leave at least one connection in the application's pool, then kill its
        // backend from a separate, non-pooled session.
        await using (var pooled = new NpgsqlConnection(connectionString))
        {
            await pooled.OpenAsync(cancellationToken);
        }

        var maintenance = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        };

        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND pid <> pg_backend_pid()
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Records the graceful-stop request without stopping the test host.</summary>
public sealed class RecordingRestartSignal : IRestoreRestartSignal
{
    private int _invocations;

    public int Invocations => Volatile.Read(ref _invocations);

    public void RequestRestart() => Interlocked.Increment(ref _invocations);
}
