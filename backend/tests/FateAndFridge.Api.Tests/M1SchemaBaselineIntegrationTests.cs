using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of the M1 schema baseline (docs/DATABASE.md §2
/// and §16). Requires a Docker daemon; run with
/// <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M1SchemaBaselineCollection.Name)]
public sealed class M1SchemaBaselineIntegrationTests(M1SchemaBaselineFixture fixture)
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private const string InitialMigrationId = "20260929032604_InitialCreate";

    private static readonly string[] BaselineTables =
    [
        "household_settings",
        "application_settings",
        "users",
        "user_permissions",
        "auth_events",
        "user_sessions",
    ];

    [Fact]
    public async Task Migration_is_applied_and_no_pending_migrations_remain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        applied.Should().Contain(migration => migration.EndsWith("M1SchemaBaseline", StringComparison.Ordinal));

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task M1_migration_rolls_back_to_InitialCreate_and_reapplies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(InitialMigrationId, cancellationToken);

        var tablesAfterRollback = await GetTableNamesAsync(context, cancellationToken);
        tablesAfterRollback.Should().BeEquivalentTo(["__EFMigrationsHistory"]);

        var appliedAfterRollback = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        appliedAfterRollback.Should().Equal(InitialMigrationId);

        await migrator.MigrateAsync(cancellationToken: cancellationToken);

        var tablesAfterReapply = await GetTableNamesAsync(context, cancellationToken);
        tablesAfterReapply.Should().Contain(BaselineTables);

        var definedMigrations = context.Database.GetMigrations();
        var appliedAfterReapply = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        appliedAfterReapply.Should().BeEquivalentTo(definedMigrations);
    }

    [Fact]
    public async Task Baseline_tables_expose_the_expected_columns_and_types()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = new Dictionary<string, Dictionary<string, (string Type, bool Nullable)>>
        {
            ["household_settings"] = new()
            {
                ["id"] = ("bigint", false),
                ["household_name"] = ("text", false),
                ["timezone"] = ("text", false),
                ["default_diner_count"] = ("integer", false),
                ["week_starts_on"] = ("integer", false),
                ["startup_page"] = ("text", false),
                ["auth_mode"] = ("text", false),
                ["created_at"] = ("timestamp with time zone", false),
                ["updated_at"] = ("timestamp with time zone", false),
            },
            ["application_settings"] = new()
            {
                ["key"] = ("text", false),
                ["value_json"] = ("jsonb", true),
                ["updated_at"] = ("timestamp with time zone", false),
            },
            ["users"] = new()
            {
                ["id"] = ("bigint", false),
                ["username"] = ("text", false),
                ["normalized_username"] = ("text", false),
                ["password_hash"] = ("text", false),
                ["must_change_password"] = ("boolean", false),
                ["role"] = ("text", false),
                ["is_active"] = ("boolean", false),
                ["locked_until"] = ("timestamp with time zone", true),
                ["failed_login_count"] = ("integer", false),
                ["last_failed_login_at"] = ("timestamp with time zone", true),
                ["lockout_count"] = ("integer", false),
                ["lockout_window_started_at"] = ("timestamp with time zone", true),
                ["created_at"] = ("timestamp with time zone", false),
                ["updated_at"] = ("timestamp with time zone", false),
                ["last_login_at"] = ("timestamp with time zone", true),
            },
            ["user_permissions"] = new()
            {
                ["id"] = ("bigint", false),
                ["user_id"] = ("bigint", false),
                ["permission_key"] = ("text", false),
            },
            ["auth_events"] = new()
            {
                ["id"] = ("bigint", false),
                ["user_id"] = ("bigint", true),
                ["username_attempted"] = ("text", true),
                ["event_type"] = ("text", false),
                ["ip_address"] = ("text", true),
                ["user_agent"] = ("text", true),
                ["occurred_at"] = ("timestamp with time zone", false),
                ["metadata_json"] = ("jsonb", true),
            },
            ["user_sessions"] = new()
            {
                ["id"] = ("bigint", false),
                ["user_id"] = ("bigint", false),
                ["token_hash"] = ("bytea", false),
                ["created_at"] = ("timestamp with time zone", false),
                ["idle_expires_at"] = ("timestamp with time zone", false),
                ["absolute_expires_at"] = ("timestamp with time zone", false),
                ["last_seen_at"] = ("timestamp with time zone", false),
                ["revoked_at"] = ("timestamp with time zone", true),
                ["revoked_reason"] = ("text", true),
                ["ip_address"] = ("text", true),
                ["user_agent"] = ("text", true),
            },
        };

        foreach (var (table, columns) in expected)
        {
            var actual = (await GetColumnsAsync(table, cancellationToken))
                .ToDictionary(column => column.ColumnName, StringComparer.Ordinal);

            actual.Keys.Should().BeEquivalentTo(
                columns.Keys,
                $"table {table} should contain exactly the approved baseline columns");

            foreach (var (column, (type, nullable)) in columns)
            {
                actual[column].DataType.Should().Be(type, $"{table}.{column} should use {type}");
                actual[column].IsNullable.Should().Be(nullable, $"{table}.{column} nullability");
            }
        }
    }

    [Fact]
    public async Task Expected_indexes_exist_with_expected_uniqueness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var indexes = (await GetIndexesAsync(cancellationToken))
            .ToDictionary(index => index.IndexName, StringComparer.Ordinal);

        AssertIndex(indexes, "ix_users_normalized_username", unique: true);
        AssertIndex(indexes, "ix_user_sessions_token_hash", unique: true);
        AssertIndex(indexes, "ix_user_permissions_user_id_permission_key", unique: true);
        AssertIndex(indexes, "ix_user_sessions_user_id", unique: false);
        AssertIndex(indexes, "ix_user_sessions_absolute_expires_at", unique: false);
        AssertIndex(indexes, "ix_auth_events_occurred_at", unique: false);
        AssertIndex(indexes, "ix_auth_events_user_id", unique: false);
    }

    [Fact]
    public async Task Unique_normalized_username_is_enforced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        context.Users.Add(NewUser("Alice", "alice"));
        await context.SaveChangesAsync(cancellationToken);

        context.Users.Add(NewUser("Alice Duplicate", "alice"));
        await AssertSaveChangesFailsWithAsync(
            PostgresErrorCodes.UniqueViolation,
            () => context.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task Unique_session_token_hash_is_enforced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        var user = NewUser("Session User", "session-user");
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        var tokenHash = new byte[] { 1, 2, 3, 4 };
        context.UserSessions.Add(NewSession(user.Id, tokenHash));
        await context.SaveChangesAsync(cancellationToken);

        context.UserSessions.Add(NewSession(user.Id, tokenHash));
        await AssertSaveChangesFailsWithAsync(
            PostgresErrorCodes.UniqueViolation,
            () => context.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task Unique_user_permission_pair_is_enforced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        var user = NewUser("Permission User", "permission-user");
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        context.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionKey = "calendar.view" });
        await context.SaveChangesAsync(cancellationToken);

        context.UserPermissions.Add(new UserPermission { UserId = user.Id, PermissionKey = "calendar.view" });
        await AssertSaveChangesFailsWithAsync(
            PostgresErrorCodes.UniqueViolation,
            () => context.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task Deleting_a_user_with_dependents_is_blocked_by_restrict_foreign_keys()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        var permissionUserId = await CreateUserAsync("Restrict Permission User", "restrict-permission-user", cancellationToken);
        await using (var context = fixture.CreateContext())
        {
            context.UserPermissions.Add(new UserPermission { UserId = permissionUserId, PermissionKey = "calendar.view" });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(user => user.Id == permissionUserId, cancellationToken);
            context.Users.Remove(user);
            await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
        }

        var sessionUserId = await CreateUserAsync("Restrict Session User", "restrict-session-user", cancellationToken);
        await using (var context = fixture.CreateContext())
        {
            context.UserSessions.Add(NewSession(sessionUserId, [5, 6, 7, 8]));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(user => user.Id == sessionUserId, cancellationToken);
            context.Users.Remove(user);
            await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task Deleting_a_user_keeps_auth_events_with_a_null_user_id()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long authEventId;

        var userId = await CreateUserAsync("Auth Event User", "auth-event-user", cancellationToken);
        await using (var context = fixture.CreateContext())
        {
            var authEvent = new AuthEvent
            {
                UserId = userId,
                UsernameAttempted = "Auth Event User",
                EventType = "login_success",
                IpAddress = "192.0.2.10",
                UserAgent = "integration-test",
                OccurredAt = FixedTimestamp,
            };
            context.AuthEvents.Add(authEvent);
            await context.SaveChangesAsync(cancellationToken);
            authEventId = authEvent.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(user => user.Id == userId, cancellationToken);
            context.Users.Remove(user);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        var reloaded = await verification.AuthEvents.SingleAsync(
            authEvent => authEvent.Id == authEventId,
            cancellationToken);

        reloaded.UserId.Should().BeNull();
        reloaded.EventType.Should().Be("login_success");
    }

    [Fact]
    public async Task Household_settings_check_constraint_rejects_a_second_row()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var firstInsert = connection.CreateCommand())
        {
            firstInsert.CommandText =
                """
                INSERT INTO household_settings
                    (id, household_name, timezone, default_diner_count, week_starts_on,
                     startup_page, auth_mode, created_at, updated_at)
                VALUES (1, 'Baseline Household', 'UTC', 4, 0, 'calendar', 'single_user', now(), now())
                """;
            await firstInsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var secondInsert = connection.CreateCommand();
        secondInsert.CommandText =
            """
            INSERT INTO household_settings
                (id, household_name, timezone, default_diner_count, week_starts_on,
                 startup_page, auth_mode, created_at, updated_at)
            VALUES (2, 'Second Household', 'UTC', 4, 0, 'calendar', 'single_user', now(), now())
            """;

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => secondInsert.ExecuteNonQueryAsync(cancellationToken));
        exception.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

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
            .Should().Be(
                unique,
                $"{indexName} should {(unique ? "be" : "not be")} a unique index");
    }

    private async Task<List<ColumnInfo>> GetColumnsAsync(string table, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Database
            .SqlQueryRaw<ColumnInfo>(
                """
                SELECT column_name,
                       data_type,
                       (is_nullable = 'YES') AS is_nullable
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = {0}
                """,
                table)
            .ToListAsync(cancellationToken);
    }

    private static async Task<List<string>> GetTableNamesAsync(
        AppDbContext context,
        CancellationToken cancellationToken) =>
        (await context.Database
            .SqlQueryRaw<TableInfo>(
                """
                SELECT table_name AS table_name
                FROM information_schema.tables
                WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
                """)
            .ToListAsync(cancellationToken))
        .Select(table => table.TableName)
        .ToList();

    private async Task<List<IndexInfo>> GetIndexesAsync(CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        return await context.Database
            .SqlQueryRaw<IndexInfo>(
                """
                SELECT indexname AS index_name,
                       indexdef AS index_definition
                FROM pg_indexes
                WHERE schemaname = 'public'
                """)
            .ToListAsync(cancellationToken);
    }

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

    private async Task<long> CreateUserAsync(
        string username,
        string normalizedUsername,
        CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        var user = NewUser(username, normalizedUsername);
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);
        return user.Id;
    }

    private static UserSession NewSession(long userId, byte[] tokenHash) => new()
    {
        UserId = userId,
        TokenHash = tokenHash,
        CreatedAt = FixedTimestamp,
        IdleExpiresAt = FixedTimestamp.AddDays(30),
        AbsoluteExpiresAt = FixedTimestamp.AddDays(90),
        LastSeenAt = FixedTimestamp,
    };

    private sealed record ColumnInfo(string ColumnName, string DataType, bool IsNullable);

    private sealed record IndexInfo(string IndexName, string IndexDefinition);

    private sealed record TableInfo(string TableName);
}
