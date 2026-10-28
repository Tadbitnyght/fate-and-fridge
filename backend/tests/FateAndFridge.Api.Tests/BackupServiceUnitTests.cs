using System.Buffers.Text;
using System.Security.Cryptography;
using FateAndFridge.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Pure unit tests for the naming, pg_dump invocation seam, and the
/// read-only recovery-key accessor
/// No database or HTTP is involved: the process invocation is a seam
/// because this workstation has no <c>pg_dump</c>.
/// </summary>
public sealed class BackupServiceUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Manual_archive_names_use_the_utc_timestamp_and_app_version()
    {
        var createdAt = new DateTimeOffset(2026, 10, 21, 14, 5, 9, TimeSpan.FromHours(2));

        var filename = BackupNaming.ForManualBackup("0.2.0", createdAt);

        filename.Should().Be("fatefridge-0.2.0-20261021T120509Z.ffbackup");
    }

    [Fact]
    public void A_filename_collision_is_disambiguated_without_losing_the_shape()
    {
        var first = BackupNaming.ForManualBackup("0.1.0", DateTimeOffset.UnixEpoch);

        BackupNaming.WithSuffix(first, 2)
            .Should().Be("fatefridge-0.1.0-19700101T000000Z-2.ffbackup");
        BackupNaming.WithSuffix(first, 3)
            .Should().Be("fatefridge-0.1.0-19700101T000000Z-3.ffbackup");
    }

    [Fact]
    public void With_suffix_rejects_attempts_below_two()
    {
        var filename = BackupNaming.ForManualBackup("0.1.0", DateTimeOffset.UnixEpoch);

        Assert.Throws<ArgumentOutOfRangeException>(() => BackupNaming.WithSuffix(filename, 1));
    }

    [Fact]
    public void Pg_dump_is_invoked_without_a_shell_and_with_the_password_in_the_environment_only()
    {
        const string password = "sup3r-s3cret-value";
        const string connectionString =
            "Host=database.internal;Port=5433;Database=fatefridge;Username=faf;Password=" + password;

        var startInfo = PgDumpRunner.BuildStartInfo(
            PgDumpRunner.DefaultExecutablePath,
            connectionString,
            "/data/backups/.tmp/dump.tmp");

        startInfo.FileName.Should().Be("pg_dump");
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.Environment["PGPASSWORD"].Should().Be(password);

        startInfo.ArgumentList.Should().ContainInOrder(
            "--format=custom", "--no-owner", "--no-privileges", "--no-password");
        startInfo.ArgumentList.Should().ContainInOrder("--file", "/data/backups/.tmp/dump.tmp");
        startInfo.ArgumentList.Should().ContainInOrder("--host", "database.internal");
        startInfo.ArgumentList.Should().ContainInOrder("--port", "5433");
        startInfo.ArgumentList.Should().ContainInOrder("--username", "faf");
        startInfo.ArgumentList.Should().ContainInOrder("--dbname", "fatefridge");

        startInfo.ArgumentList.Should().NotContain(
            argument => argument.Contains(password, StringComparison.Ordinal),
            "the password must never be passed as a process argument");
    }

    [Fact]
    public async Task A_missing_pg_dump_reports_a_clear_error_without_leaking_the_password()
    {
        const string password = "another-s3cret-value";
        var runner = new PgDumpRunner(
            $"Host=localhost;Port=5432;Database=fatefridge;Username=faf;Password={password}",
            "/nonexistent/fate-and-fridge-test/pg_dump",
            TimeSpan.FromSeconds(5),
            NullLogger<PgDumpRunner>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(Path.Combine(Path.GetTempPath(), $"dump-{Guid.NewGuid():N}.tmp"), Ct));

        exception.Message.Should().Contain("pg_dump");
        exception.Message.Should().NotContain(password);
    }

    [Fact]
    public async Task Reading_the_recovery_key_never_creates_one()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"faf-key-{Guid.NewGuid():N}");
        try
        {
            var store = CreateStore(dataDir);

            (await store.TryReadAsync(Ct)).Should().BeNull();
            File.Exists(store.FilePath).Should().BeFalse("a backup must never mint a recovery key");

            var read = await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(Ct));
            read.Message.Should().Contain("does not exist");
        }
        finally
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Reading_an_existing_recovery_key_returns_it_without_modifying_the_file()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"faf-key-{Guid.NewGuid():N}");
        try
        {
            var store = CreateStore(dataDir);
            var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(RecoveryKeyStore.KeyLengthBytes));
            Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
            await File.WriteAllTextAsync(store.FilePath, key, Ct);

            (await store.TryReadAsync(Ct)).Should().Be(key);
            (await store.ReadAsync(Ct)).Should().Be(key);
            (await File.ReadAllTextAsync(store.FilePath, Ct)).Should().Be(key);
        }
        finally
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    private static RecoveryKeyStore CreateStore(string dataDir)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = dataDir })
            .Build();

        return new RecoveryKeyStore(configuration, NullLogger<RecoveryKeyStore>.Instance);
    }
}
