using System.Runtime.Versioning;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Unit checks for the tooling probe: a missing, unparseable, or failing
/// pg_dump (and an unavailable server probe) must be reported as unsupported
/// and must never throw.
/// </summary>
public sealed class PgToolingInfoTests
{
    [Fact]
    public async Task Missing_pg_dump_is_reported_without_throwing()
    {
        var status = await CreateProbe(clientOutput: null).ProbeAsync(TestContext.Current.CancellationToken);

        status.ClientVersion.Should().BeNull();
        status.ClientMajor.Should().BeNull();
        status.ServerMajor.Should().Be(17);
        status.SupportsBackup.Should().BeFalse();
    }

    [Fact]
    public async Task Unparseable_pg_dump_output_is_reported_without_throwing()
    {
        var status = await CreateProbe(clientOutput: "pg_dump: command not found")
            .ProbeAsync(TestContext.Current.CancellationToken);

        status.ClientVersion.Should().Be("pg_dump: command not found");
        status.ClientMajor.Should().BeNull();
        status.SupportsBackup.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_pg_dump_probe_is_tolerated()
    {
        var status = await CreateProbe(
                clientOutput: null,
                clientFailure: new InvalidOperationException("probe exploded"))
            .ProbeAsync(TestContext.Current.CancellationToken);

        status.ClientMajor.Should().BeNull();
        status.SupportsBackup.Should().BeFalse();
    }

    [Fact]
    public async Task A_failing_server_probe_is_tolerated()
    {
        var probe = new PgToolingInfo(
            new FakePgDumpVersionReader("pg_dump (PostgreSQL) 17.4"),
            new FakePgServerVersionReader(
                new PgServerVersion(null, null),
                new InvalidOperationException("probe exploded")),
            NullLogger<PgToolingInfo>.Instance);

        var status = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        status.ClientMajor.Should().Be(17);
        status.ServerMajor.Should().BeNull();
        status.SupportsBackup.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_executable_yields_no_version()
    {
        var reader = new PgDumpVersionReader(
            Path.Combine(Path.GetTempPath(), $"missing-pg_dump-{Guid.NewGuid():N}"),
            TimeSpan.FromSeconds(5));

        var output = await reader.ReadVersionOutputAsync(TestContext.Current.CancellationToken);

        output.Should().BeNull();
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task The_reader_runs_pg_dump_without_a_shell_and_returns_its_output()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The pg_dump probe is a POSIX executable test.");
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "fate-and-fridge-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "pg_dump");
        await File.WriteAllTextAsync(
            executable,
            "#!/bin/sh\necho 'pg_dump (PostgreSQL) 17.4 (Debian 17.4-1.pgdg120+1)'\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try
        {
            var reader = new PgDumpVersionReader(executable, TimeSpan.FromSeconds(5));

            var output = await reader.ReadVersionOutputAsync(TestContext.Current.CancellationToken);

            output.Should().NotBeNull();
            PgVersionParser.TryParseClientMajor(output, out var major).Should().BeTrue();
            major.Should().Be(17);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PgToolingInfo CreateProbe(string? clientOutput, Exception? clientFailure = null) =>
        new(
            new FakePgDumpVersionReader(clientOutput, clientFailure),
            new FakePgServerVersionReader(new PgServerVersion(17, "17.4")),
            NullLogger<PgToolingInfo>.Instance);
}
