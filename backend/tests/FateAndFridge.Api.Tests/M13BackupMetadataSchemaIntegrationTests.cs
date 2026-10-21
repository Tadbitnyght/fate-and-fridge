using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Services;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of the M13-01 <c>backup_metadata</c> schema
/// (docs/DATABASE.md §12; docs/M13_PLAN.md §1.7) plus the server-major probe.
/// Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M13BackupMetadataCollection.Name)]
public sealed class M13BackupMetadataSchemaIntegrationTests(M13BackupMetadataFixture fixture)
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static string Sha256Hex => new('a', 64);

    [Fact]
    public async Task Migration_is_applied_from_scratch_and_no_pending_migrations_remain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        applied.Should().Contain(migration => migration.EndsWith("M13Backups", StringComparison.Ordinal));

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task Backup_metadata_exposes_the_expected_columns_and_types()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = new Dictionary<string, ExpectedColumn>
        {
            ["id"] = new("bigint", false),
            ["filename"] = new("character varying", false, MaxLength: 255),
            ["app_version"] = new("character varying", false, MaxLength: 50),
            ["schema_version"] = new("character varying", false, MaxLength: 150),
            ["created_at"] = new("timestamp with time zone", false),
            ["created_by_user_id"] = new("bigint", true),
            ["checksum"] = new("character varying", false, MaxLength: 64),
            ["size_bytes"] = new("bigint", false),
            ["kind"] = new("text", false),
            ["status"] = new("text", false),
            ["error"] = new("text", true),
        };

        var actual = (await GetColumnsAsync(cancellationToken))
            .ToDictionary(column => column.ColumnName, StringComparer.Ordinal);

        actual.Keys.Should().BeEquivalentTo(
            expected.Keys,
            "backup_metadata should contain exactly the approved M13 columns");

        foreach (var (column, expectation) in expected)
        {
            actual[column].DataType.Should().Be(expectation.Type, $"{column} should use {expectation.Type}");
            actual[column].IsNullable.Should().Be(expectation.Nullable, $"{column} nullability");
            actual[column].CharacterMaximumLength.Should().Be(
                expectation.MaxLength,
                $"{column} character length");
        }
    }

    [Fact]
    public async Task Expected_backup_indexes_exist_with_expected_uniqueness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var indexes = (await GetIndexesAsync(cancellationToken))
            .ToDictionary(index => index.IndexName, StringComparer.Ordinal);

        AssertIndex(indexes, "pk_backup_metadata", unique: true);
        AssertIndex(indexes, "ix_backup_metadata_created_at", unique: false);
        AssertIndex(indexes, "ix_backup_metadata_created_by_user_id", unique: false);
        AssertIndex(indexes, "ix_backup_metadata_filename", unique: true);
    }

    [Fact]
    public async Task Backup_filename_is_unique()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string filename = "fatefridge-0.1.0-20261020T120000Z.ffbackup";

        await using (var context = fixture.CreateContext())
        {
            context.BackupMetadata.Add(NewBackup(filename));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.BackupMetadata.Add(NewBackup(filename));
            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("ix_backup_metadata_filename");
        }
    }

    [Fact]
    public async Task Backup_kind_and_status_round_trip_as_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long backupId;

        await using (var context = fixture.CreateContext())
        {
            var backup = NewBackup("fatefridge-0.1.0-safety.ffbackup");
            backup.Kind = BackupKind.Safety;
            backup.Status = BackupStatus.Restored;
            context.BackupMetadata.Add(backup);
            await context.SaveChangesAsync(cancellationToken);
            backupId = backup.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var stored = await context.BackupMetadata
                .SingleAsync(backup => backup.Id == backupId, cancellationToken);

            stored.Kind.Should().Be(BackupKind.Safety);
            stored.Status.Should().Be(BackupStatus.Restored);

            var columnValues = await context.Database
                .SqlQueryRaw<BackupTextColumns>(
                    "SELECT kind, status FROM backup_metadata WHERE id = {0}",
                    backupId)
                .SingleAsync(cancellationToken);

            columnValues.Kind.Should().Be("safety");
            columnValues.Status.Should().Be("restored");
        }
    }

    [Fact]
    public async Task Deleting_a_user_who_created_a_backup_is_blocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long userId;

        await using (var context = fixture.CreateContext())
        {
            var user = NewUser("M13 Backup User", "m13-backup-user");
            context.Users.Add(user);
            await context.SaveChangesAsync(cancellationToken);
            userId = user.Id;

            var backup = NewBackup("fatefridge-0.1.0-restrict.ffbackup");
            backup.CreatedByUserId = userId;
            context.BackupMetadata.Add(backup);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(
                candidate => candidate.Id == userId, cancellationToken);
            context.Users.Remove(user);

            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("fk_backup_metadata_users_created_by_user_id");
        }
    }

    [Fact]
    public async Task Server_major_probe_reports_the_container_version()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        var probe = new PgToolingInfo(
            new FakePgDumpVersionReader("pg_dump (PostgreSQL) 17.4 (Debian 17.4-1.pgdg120+1)"),
            new NpgsqlServerVersionReader(context),
            NullLogger<PgToolingInfo>.Instance);

        var status = await probe.ProbeAsync(cancellationToken);

        status.ClientMajor.Should().Be(17);
        status.ServerMajor.Should().Be(17);
        status.ServerVersion.Should().NotBeNullOrWhiteSpace();
        status.SupportsBackup.Should().BeTrue();
    }

    private static BackupMetadata NewBackup(string filename) => new()
    {
        Filename = filename,
        AppVersion = "0.1.0",
        SchemaVersion = "20261021023131_M13Backups",
        CreatedAt = FixedTimestamp,
        Checksum = Sha256Hex,
        SizeBytes = 1024,
        Kind = BackupKind.Manual,
        Status = BackupStatus.Pending,
    };

    private static User NewUser(string username, string normalizedUsername) => new()
    {
        Username = username,
        NormalizedUsername = normalizedUsername,
        PasswordHash = "test-hash-not-a-real-password",
        Role = UserRole.Member,
        IsActive = true,
        CreatedAt = FixedTimestamp,
        UpdatedAt = FixedTimestamp,
    };

    private static async Task<PostgresException> AssertSaveChangesFailsWithAsync(
        string expectedSqlState,
        Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<DbUpdateException>(action);
        var postgresException = exception.InnerException as PostgresException;
        postgresException.Should().NotBeNull(
            $"expected PostgreSQL error {expectedSqlState}, but got: {exception}");

        postgresException!.SqlState.Should().Be(expectedSqlState);
        return postgresException;
    }

    private static void AssertIndex(
        IReadOnlyDictionary<string, IndexInfo> indexes,
        string indexName,
        bool unique)
    {
        indexes.Should().ContainKey(indexName);
        indexes[indexName].IndexDefinition
            .StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal)
            .Should().Be(unique, $"{indexName} should {(unique ? "be" : "not be")} a unique index");
    }

    private async Task<List<ColumnInfo>> GetColumnsAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Database
            .SqlQueryRaw<ColumnInfo>(
                """
                SELECT column_name,
                       data_type,
                       (is_nullable = 'YES') AS is_nullable,
                       character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = 'backup_metadata'
                """)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<IndexInfo>> GetIndexesAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Database
            .SqlQueryRaw<IndexInfo>(
                """
                SELECT indexname AS index_name,
                       indexdef AS index_definition
                FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'backup_metadata'
                """)
            .ToListAsync(cancellationToken);
    }

    private sealed record ExpectedColumn(string Type, bool Nullable, int? MaxLength = null);

    private sealed record ColumnInfo(
        string ColumnName,
        string DataType,
        bool IsNullable,
        int? CharacterMaximumLength);

    private sealed record IndexInfo(string IndexName, string IndexDefinition);

    private sealed record BackupTextColumns(string Kind, string Status);
}
