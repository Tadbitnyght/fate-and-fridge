using FateAndFridge.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit tests for the <c>pg_restore</c> seam. No database and no process is started: the invocation is a
/// seam because this workstation ships no PostgreSQL client tools, and the real
/// binary is verified in the runtime image at.
/// </summary>
public sealed class PgRestoreRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Pg_restore_is_invoked_without_a_shell_and_with_the_password_in_the_environment_only()
    {
        const string password = "sup3r-s3cret-value";
        const string connectionString =
            "Host=database.internal;Port=5433;Database=fatefridge;Username=faf;Password=" + password;

        var startInfo = PgRestoreRunner.BuildStartInfo(
            PgRestoreRunner.DefaultExecutablePath,
            connectionString,
            "/data/backups/.restore/abc/staging/database.dump");

        startInfo.FileName.Should().Be("pg_restore");
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.Environment["PGPASSWORD"].Should().Be(password);

        // The restore replaces the existing database and must roll back as one
        // transaction.
        startInfo.ArgumentList.Should().ContainInOrder(
            "--clean",
            "--if-exists",
            "--no-owner",
            "--no-privileges",
            "--single-transaction",
            "--no-password",
            "--dbname=fatefridge");
        startInfo.ArgumentList.Should().ContainInOrder("--host", "database.internal");
        startInfo.ArgumentList.Should().ContainInOrder("--port", "5433");
        startInfo.ArgumentList.Should().ContainInOrder("--username", "faf");
        startInfo.ArgumentList[^1].Should().Be("/data/backups/.restore/abc/staging/database.dump");

        startInfo.ArgumentList.Should().NotContain(
            argument => argument.Contains(password, StringComparison.Ordinal),
            "the password must never be passed as a process argument");
    }

    [Fact]
    public void A_connection_string_without_a_database_is_refused()
    {
        var exception = Assert.Throws<ArgumentException>(() => PgRestoreRunner.BuildStartInfo(
            PgRestoreRunner.DefaultExecutablePath,
            "Host=localhost;Username=faf;Password=secret",
            "/data/staging/database.dump"));

        exception.Message.Should().Contain("database");
    }

    [Fact]
    public void The_termination_query_runs_against_the_postgres_maintenance_database()
    {
        var connectionString = PgRestoreRunner.BuildMaintenanceConnectionString(
            "Host=database.internal;Port=5433;Database=fatefridge;Username=faf;Password=secret;SSL Mode=Disable");

        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        settings.Database.Should().Be("postgres");
        settings.Host.Should().Be("database.internal");
        settings.Port.Should().Be(5433);
        settings.Username.Should().Be("faf");
        settings.Password.Should().Be("secret");
        settings.Pooling.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_pg_restore_reports_a_clear_error_without_leaking_the_password()
    {
        const string password = "another-s3cret-value";
        var runner = new PgRestoreRunner(
            $"Host=localhost;Port=5432;Database=fatefridge;Username=faf;Password={password}",
            "/nonexistent/fate-and-fridge-test/pg_restore",
            TimeSpan.FromSeconds(5),
            NullLogger<PgRestoreRunner>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RestoreAsync(
                Path.Combine(Path.GetTempPath(), $"restore-{Guid.NewGuid():N}.dump"),
                Ct));

        exception.Message.Should().Contain("pg_restore");
        exception.Message.Should().NotContain(password);
    }

    [Fact]
    public async Task Terminating_connections_with_a_bad_host_is_an_invalid_operation_not_a_leak()
    {
        const string password = "third-s3cret-value";
        var runner = new PgRestoreRunner(
            $"Host=127.0.0.1;Port=1;Database=fatefridge;Username=faf;Password={password};Timeout=1",
            PgRestoreRunner.DefaultExecutablePath,
            TimeSpan.FromSeconds(5),
            NullLogger<PgRestoreRunner>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.TerminateConnectionsAsync(Ct));

        exception.Message.Should().Contain("terminated");
        exception.Message.Should().NotContain(password);
    }
}
