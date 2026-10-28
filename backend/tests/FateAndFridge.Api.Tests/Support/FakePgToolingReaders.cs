using FateAndFridge.Api.Services;

namespace FateAndFridge.Api.Tests.Support;

/// <summary>
/// Deterministic pg_dump probe for the tooling tests: returns fixed
/// output, or a fixed failure, without starting a process.
/// </summary>
public sealed class FakePgDumpVersionReader(string? output, Exception? failure = null) : IPgDumpVersionReader
{
    public Task<string?> ReadVersionOutputAsync(CancellationToken cancellationToken) =>
        failure is null
            ? Task.FromResult(output)
            : Task.FromException<string?>(failure);
}

/// <summary>Deterministic PostgreSQL server version probe for the tests.</summary>
public sealed class FakePgServerVersionReader(PgServerVersion version, Exception? failure = null)
    : IPgServerVersionReader
{
    public Task<PgServerVersion> ReadVersionAsync(CancellationToken cancellationToken) =>
        failure is null
            ? Task.FromResult(version)
            : Task.FromException<PgServerVersion>(failure);
}
