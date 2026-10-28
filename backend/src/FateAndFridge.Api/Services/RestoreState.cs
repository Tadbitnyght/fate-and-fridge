namespace FateAndFridge.Api.Services;

/// <summary>
/// The seam through which a finished restore asks the host to stop so
/// <c>restart: unless-stopped</c> restarts the container
/// It is a seam so the orchestration can be tested
/// without stopping the test host.
/// </summary>
public interface IRestoreRestartSignal
{
    /// <summary>
    /// Requests a graceful application stop. The caller must already have
    /// responded to the restore request and written the restore marker.
    /// </summary>
    void RequestRestart();
}

/// <summary>
/// Production <see cref="IRestoreRestartSignal"/>: stops this process through
/// the host lifetime so the container's restart policy brings it back
/// Nothing here touches the database or the files.
/// </summary>
public sealed class HostRestartSignal(
    IHostApplicationLifetime lifetime,
    ILogger<HostRestartSignal> logger) : IRestoreRestartSignal
{
    public void RequestRestart()
    {
        logger.LogWarning(
            "A restore completed; requesting a graceful application stop so the container restarts.");
        lifetime.StopApplication();
    }
}

/// <summary>
/// Process-local restore state: the maintenance flag and the progress of the
/// restore that owns it. A singleton, because
/// maintenance mode must be visible to every request and there is exactly one
/// restore lane per instance (the backup operation gate serializes it).
/// </summary>
/// <remarks>
/// Nothing here is persisted. If the process dies mid-restore the flag
/// disappears with it, which is the desired outcome: the container restarts
/// with a clean state, and the restore marker on disk records whether the
/// interruption happened after the final step.
/// </remarks>
public sealed class RestoreState
{
    private readonly object _sync = new();
    private bool _maintenance;
    private string? _restoreId;
    private string _phase = "idle";
    private string? _lastError;
    private DateTimeOffset? _startedAt;
    private Task? _execution;

    /// <summary>
    /// True while a restore holds maintenance mode: every route except
    /// <c>/api/v1/health*</c> answers <c>503 RESTORE_IN_PROGRESS</c>.
    /// </summary>
    public bool IsMaintenanceActive
    {
        get
        {
            lock (_sync)
            {
                return _maintenance;
            }
        }
    }

    /// <summary>The restore that is running or ran most recently, if any.</summary>
    public string? RestoreId
    {
        get
        {
            lock (_sync)
            {
                return _restoreId;
            }
        }
    }

    /// <summary>A coarse, log-friendly name for the current step.</summary>
    public string Phase
    {
        get
        {
            lock (_sync)
            {
                return _phase;
            }
        }
    }

    /// <summary>The last failure detail, cleared when a new restore starts.</summary>
    public string? LastError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    /// <summary>When the current (or last) restore started.</summary>
    public DateTimeOffset? StartedAt
    {
        get
        {
            lock (_sync)
            {
                return _startedAt;
            }
        }
    }

    /// <summary>
    /// The background execution of the current (or last) restore. It is set
    /// synchronously before the <c>202</c> response is written, so an observer
    /// can await the outcome.
    /// </summary>
    public Task Execution
    {
        get
        {
            lock (_sync)
            {
                return _execution ?? Task.CompletedTask;
            }
        }
    }

    /// <summary>Starts a new restore: clears the previous outcome and phase.</summary>
    public void Begin(string restoreId, DateTimeOffset startedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);

        lock (_sync)
        {
            _restoreId = restoreId;
            _startedAt = startedAt;
            _phase = "validating";
            _lastError = null;
            _execution = null;
        }
    }

    /// <summary>Records the background task that runs the restore.</summary>
    public void BeginExecution(Task execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        lock (_sync)
        {
            _execution = execution;
        }
    }

    /// <summary>Records progress; the phase is informational only.</summary>
    public void MarkPhase(string phase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);

        lock (_sync)
        {
            _phase = phase;
        }
    }

    /// <summary>Enters maintenance mode for <paramref name="restoreId"/>.</summary>
    public void EnterMaintenance(string restoreId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreId);

        lock (_sync)
        {
            _restoreId = restoreId;
            _phase = "maintenance";
            _maintenance = true;
        }
    }

    /// <summary>Leaves maintenance mode (idempotent).</summary>
    public void ExitMaintenance()
    {
        lock (_sync)
        {
            _maintenance = false;
        }
    }

    /// <summary>Marks the restore completed; maintenance is already cleared.</summary>
    public void MarkCompleted()
    {
        lock (_sync)
        {
            _phase = "completed";
            _lastError = null;
        }
    }

    /// <summary>
    /// Marks the restore failed. The original data is intact; the caller is
    /// responsible for having rolled back its own steps.
    /// </summary>
    public void MarkFailed(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        lock (_sync)
        {
            _phase = "failed";
            _lastError = error;
        }
    }
}
