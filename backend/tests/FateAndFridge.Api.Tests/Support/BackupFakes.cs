using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Deterministic <see cref="IPgDumpRunner"/> for the tests. The
/// development workstation has no <c>pg_dump</c>, so the orchestration is
/// proven with this fake; the real binary is covered by the conditional
/// integration test and the image gate.
/// </summary>
public sealed class RecordingPgDumpRunner : IPgDumpRunner
{
    private readonly Func<string, CancellationToken, Task> _behaviour;

    private RecordingPgDumpRunner(Func<string, CancellationToken, Task> behaviour)
    {
        _behaviour = behaviour;
    }

    /// <summary>Every output path the service asked for, in call order.</summary>
    public List<string> OutputPaths { get; } = [];

    /// <summary>Writes <paramref name="content"/> and succeeds.</summary>
    public static RecordingPgDumpRunner Writing(byte[] content) =>
        new(async (path, cancellationToken) =>
            await File.WriteAllBytesAsync(path, content, cancellationToken));

    /// <summary>Writes a partial file and then fails, as a crashed client would.</summary>
    public static RecordingPgDumpRunner FailingAfterPartialWrite(string message) =>
        new(async (path, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(path, new byte[256], cancellationToken);
            throw new InvalidOperationException(message);
        });

    /// <summary>Fails before writing anything.</summary>
    public static RecordingPgDumpRunner Failing(string message) =>
        new((_, _) => throw new InvalidOperationException(message));

    public async Task RunAsync(string outputPath, CancellationToken cancellationToken)
    {
        OutputPaths.Add(outputPath);
        await _behaviour(outputPath, cancellationToken);
    }
}

/// <summary>
/// A runner that blocks inside the dump until the test releases it, so a second
/// create can be attempted while the first one is provably in flight.
/// </summary>
public sealed class GatedPgDumpRunner : IPgDumpRunner
{
    public TaskCompletionSource Started { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _invocations;

    public int Invocations => _invocations;

    public async Task RunAsync(string outputPath, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _invocations);
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        await File.WriteAllBytesAsync(
            outputPath,
            "fake custom-format dump"u8.ToArray(),
            cancellationToken);
    }
}
