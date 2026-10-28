using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// A deterministic write gate for the concurrency battery. The first
/// command whose text matches <c>commandTextPattern</c> signals
/// <see cref="Reached"/> and then blocks until <see cref="Release"/> is called.
/// Register it on an <c>AppDbContext</c> rebuilt with
/// <c>AddInterceptors</c> so exactly one request can be paused mid-write while
/// the test forces the opposite interleaving. No sleeps are involved.
/// </summary>
public sealed class GatedDbCommandInterceptor(string commandTextPattern) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _reached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _gated;

    /// <summary>Completes when the matching command is about to execute.</summary>
    public Task Reached => _reached.Task;

    /// <summary>Lets the paused command (and every later match) proceed.</summary>
    public void Release() => _release.TrySetResult();

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await GateAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await GateAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await GateAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask GateAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!command.CommandText.Contains(commandTextPattern, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Interlocked.Exchange(ref _gated, 1) == 0)
        {
            _reached.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// A two-party barrier for duplicate-submit races: the request pair only
/// proceeds once <b>both</b> have reached the matching command, so both have
/// already passed their application-level pre-checks when the database is
/// touched. This forces the lost-update / unique-violation interleaving without
/// timing assumptions.
/// </summary>
public sealed class BarrierDbCommandInterceptor(string commandTextPattern, int parties = 2)
    : DbCommandInterceptor
{
    private readonly TaskCompletionSource _allArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _arrived;

    /// <summary>How many matching commands have reached the barrier.</summary>
    public int Arrived => Volatile.Read(ref _arrived);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await WaitAsync(command, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async ValueTask WaitAsync(DbCommand command, CancellationToken cancellationToken)
    {
        if (!command.CommandText.Contains(commandTextPattern, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Interlocked.Increment(ref _arrived) >= parties)
        {
            _allArrived.TrySetResult();
        }

        await _allArrived.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
