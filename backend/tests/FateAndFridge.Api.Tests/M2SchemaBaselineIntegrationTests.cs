using System.Text.Json;
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
/// PostgreSQL-backed verification of the M2 reference-data and audit schema
/// baseline (docs/DATABASE.md §4, §5, §7, §10). Requires a Docker daemon; run
/// with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M2SchemaBaselineCollection.Name)]
public sealed class M2SchemaBaselineIntegrationTests(M2SchemaBaselineFixture fixture)
{
    private const string M1MigrationId = "20260930015712_M1SchemaBaseline";

    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] M1Tables =
    [
        "household_settings",
        "application_settings",
        "users",
        "user_permissions",
        "auth_events",
        "user_sessions",
    ];

    private static readonly string[] M2Tables =
    [
        "grocery_departments",
        "ingredients",
        "ingredient_aliases",
        "units",
        "unit_aliases",
        "proteins",
        "tags",
        "audit_events",
    ];

    [Fact]
    public async Task Migration_is_applied_from_scratch_and_no_pending_migrations_remain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        applied.Should().Contain(migration => migration.EndsWith("InitialCreate", StringComparison.Ordinal));
        applied.Should().Contain(migration => migration.EndsWith("M1SchemaBaseline", StringComparison.Ordinal));
        applied.Should().Contain(migration => migration.EndsWith("M2ReferenceDataBaseline", StringComparison.Ordinal));

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task M2_migration_applies_to_an_M1_baseline_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();

        // A database that only ran the M1 baseline must upgrade cleanly.
        await migrator.MigrateAsync(M1MigrationId, cancellationToken);

        var tablesAtM1 = await GetTableNamesAsync(context, cancellationToken);
        tablesAtM1.Should().Contain(M1Tables);
        tablesAtM1.Intersect(M2Tables).Should().BeEmpty("M2 tables must not exist before the M2 migration runs");

        await migrator.MigrateAsync(cancellationToken: cancellationToken);

        var tablesAfterUpgrade = await GetTableNamesAsync(context, cancellationToken);
        tablesAfterUpgrade.Should().Contain(M2Tables);
    }

    [Fact]
    public async Task M2_tables_expose_the_expected_columns_and_types()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = new Dictionary<string, Dictionary<string, ExpectedColumn>>
        {
            ["grocery_departments"] = new()
            {
                ["id"] = new("bigint", false),
                ["name"] = new("text", false),
                ["normalized_name"] = new("text", false),
                ["sort_order"] = new("integer", false),
                ["is_active"] = new("boolean", false),
                ["created_at"] = new("timestamp with time zone", false),
                ["updated_at"] = new("timestamp with time zone", false),
            },
            ["ingredients"] = new()
            {
                ["id"] = new("bigint", false),
                ["canonical_name"] = new("text", false),
                ["normalized_name"] = new("text", false),
                ["department_id"] = new("bigint", true),
                ["merged_into_ingredient_id"] = new("bigint", true),
                ["is_active"] = new("boolean", false),
                ["created_at"] = new("timestamp with time zone", false),
                ["updated_at"] = new("timestamp with time zone", false),
            },
            ["ingredient_aliases"] = new()
            {
                ["id"] = new("bigint", false),
                ["ingredient_id"] = new("bigint", false),
                ["alias"] = new("text", false),
                ["normalized_alias"] = new("text", false),
            },
            ["units"] = new()
            {
                ["id"] = new("bigint", false),
                ["symbol"] = new("text", false),
                ["display_name"] = new("text", false),
                ["normalized_name"] = new("text", false),
                ["dimension"] = new("text", false),
                ["base_factor"] = new("numeric", true, Precision: 28, Scale: 14),
                ["is_custom"] = new("boolean", false),
                ["is_active"] = new("boolean", false),
            },
            ["unit_aliases"] = new()
            {
                ["id"] = new("bigint", false),
                ["unit_id"] = new("bigint", false),
                ["alias"] = new("text", false),
                ["normalized_alias"] = new("text", false),
            },
            ["proteins"] = new()
            {
                ["id"] = new("bigint", false),
                ["name"] = new("text", false),
                ["normalized_name"] = new("text", false),
                ["is_active"] = new("boolean", false),
            },
            ["tags"] = new()
            {
                ["id"] = new("bigint", false),
                ["name"] = new("text", false),
                ["normalized_name"] = new("text", false),
            },
            ["audit_events"] = new()
            {
                ["id"] = new("bigint", false),
                ["actor_user_id"] = new("bigint", true),
                ["event_type"] = new("text", false),
                ["entity_type"] = new("text", false),
                ["entity_id"] = new("text", true),
                ["before_json"] = new("jsonb", true),
                ["after_json"] = new("jsonb", true),
                ["reason"] = new("text", true),
                ["ip_address"] = new("text", true),
                ["created_at"] = new("timestamp with time zone", false),
                ["metadata_json"] = new("jsonb", false),
            },
        };

        foreach (var (table, columns) in expected)
        {
            var actual = (await GetColumnsAsync(table, cancellationToken))
                .ToDictionary(column => column.ColumnName, StringComparer.Ordinal);

            actual.Keys.Should().BeEquivalentTo(
                columns.Keys,
                $"table {table} should contain exactly the approved M2 columns");

            foreach (var (column, expectation) in columns)
            {
                actual[column].DataType.Should().Be(expectation.Type, $"{table}.{column} should use {expectation.Type}");
                actual[column].IsNullable.Should().Be(expectation.Nullable, $"{table}.{column} nullability");

                // information_schema reports a synthetic precision for integer
                // types too, so precision/scale are only meaningful for numeric.
                if (expectation.Type == "numeric")
                {
                    actual[column].NumericPrecision.Should().Be(expectation.Precision, $"{table}.{column} precision");
                    actual[column].NumericScale.Should().Be(expectation.Scale, $"{table}.{column} scale");
                }
            }
        }
    }

    [Fact]
    public async Task Expected_indexes_exist_with_expected_uniqueness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var indexes = (await GetIndexesAsync(cancellationToken))
            .ToDictionary(index => index.IndexName, StringComparer.Ordinal);

        AssertIndex(indexes, "ix_grocery_departments_normalized_name", unique: true);
        AssertIndex(indexes, "ix_ingredients_normalized_name", unique: true);
        AssertIndex(indexes, "ix_ingredient_aliases_normalized_alias", unique: true);
        AssertIndex(indexes, "ix_units_symbol", unique: true);
        AssertIndex(indexes, "ix_units_normalized_name", unique: true);
        AssertIndex(indexes, "ix_unit_aliases_unit_id_normalized_alias", unique: true);
        AssertIndex(indexes, "ix_proteins_normalized_name", unique: true);
        AssertIndex(indexes, "ix_tags_normalized_name", unique: true);
        AssertIndex(indexes, "ix_audit_events_created_at", unique: false);
        AssertIndex(indexes, "ix_audit_events_actor_user_id", unique: false);
        AssertIndex(indexes, "ix_audit_events_entity_type_entity_id", unique: false);
    }

    [Fact]
    public async Task Unique_normalized_names_are_enforced_for_departments_ingredients_proteins_and_tags()
    {
        await AssertUniqueViolationAsync(
            NewDepartment("M2 Produce One", "m2-produce-one"),
            NewDepartment("M2 Produce Two", "m2-produce-one"));

        await AssertUniqueViolationAsync(
            NewIngredient("M2 Chicken One", "m2-chicken-one"),
            NewIngredient("M2 Chicken Two", "m2-chicken-one"));

        await AssertUniqueViolationAsync(
            NewProtein("M2 Beef One", "m2-beef-one"),
            NewProtein("M2 Beef Two", "m2-beef-one"));

        await AssertUniqueViolationAsync(
            NewTag("M2 Quick One", "m2-quick-one"),
            NewTag("M2 Quick Two", "m2-quick-one"));
    }

    [Fact]
    public async Task Unit_symbol_and_normalized_name_are_unique()
    {
        await AssertUniqueViolationAsync(
            NewUnit("m2-sym-dup", "M2 Ounce One", "m2-ounce-one", UnitDimension.Mass, 28.349523125m),
            NewUnit("m2-sym-dup", "M2 Ounce Two", "m2-ounce-two", UnitDimension.Mass, 28.349523125m));

        await AssertUniqueViolationAsync(
            NewUnit("m2-oz-one", "M2 Ounce Three", "m2-ounce-dup", UnitDimension.Mass, 28.349523125m),
            NewUnit("m2-oz-two", "M2 Ounce Four", "m2-ounce-dup", UnitDimension.Mass, 28.349523125m));
    }

    [Fact]
    public async Task Ingredient_alias_normalized_alias_is_globally_unique()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long firstIngredientId;
        long secondIngredientId;

        await using (var context = fixture.CreateContext())
        {
            var first = NewIngredient("M2 Alias Chicken One", "m2-alias-chicken-one");
            var second = NewIngredient("M2 Alias Chicken Two", "m2-alias-chicken-two");
            context.Ingredients.AddRange(first, second);
            await context.SaveChangesAsync(cancellationToken);
            firstIngredientId = first.Id;
            secondIngredientId = second.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            context.IngredientAliases.Add(new IngredientAlias
            {
                IngredientId = firstIngredientId,
                Alias = "M2 chick",
                NormalizedAlias = "m2-chick",
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.IngredientAliases.Add(new IngredientAlias
            {
                IngredientId = secondIngredientId,
                Alias = "M2 CHICK",
                NormalizedAlias = "m2-chick",
            });
            await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task Unit_alias_uniqueness_is_per_unit_and_allows_the_seeded_t_and_T_aliases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long teaspoonId;
        long tablespoonId;

        await using (var context = fixture.CreateContext())
        {
            var teaspoon = NewUnit("m2-tsp", "M2 teaspoon", "m2-teaspoon", UnitDimension.Volume, 4.92892159375m);
            var tablespoon = NewUnit("m2-tbsp", "M2 tablespoon", "m2-tablespoon", UnitDimension.Volume, 14.78676478125m);
            context.Units.AddRange(teaspoon, tablespoon);
            await context.SaveChangesAsync(cancellationToken);
            teaspoonId = teaspoon.Id;
            tablespoonId = tablespoon.Id;
        }

        // The authoritative seed has "t" (teaspoon) and "T" (tablespoon); both
        // normalize to "t" and must coexist on different units (M2_PLAN §2).
        await using (var context = fixture.CreateContext())
        {
            context.UnitAliases.AddRange(
                new UnitAlias { UnitId = teaspoonId, Alias = "t", NormalizedAlias = "t" },
                new UnitAlias { UnitId = tablespoonId, Alias = "T", NormalizedAlias = "t" });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.UnitAliases.Add(new UnitAlias
            {
                UnitId = teaspoonId,
                Alias = "t",
                NormalizedAlias = "t",
            });
            await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
        }
    }

    [Fact]
    public async Task Deleting_a_merge_target_is_blocked_by_the_self_referencing_restrict_foreign_key()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long targetId;
        long sourceId;

        await using (var context = fixture.CreateContext())
        {
            var target = NewIngredient("M2 Merge Target", "m2-merge-target");
            context.Ingredients.Add(target);
            await context.SaveChangesAsync(cancellationToken);

            var source = NewIngredient("M2 Merge Source", "m2-merge-source");
            source.MergedIntoIngredientId = target.Id;
            context.Ingredients.Add(source);
            await context.SaveChangesAsync(cancellationToken);

            targetId = target.Id;
            sourceId = source.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var target = await context.Ingredients.SingleAsync(
                ingredient => ingredient.Id == targetId,
                cancellationToken);
            context.Ingredients.Remove(target);

            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("fk_ingredients_ingredients_merged_into_ingredient_id");
        }

        await using (var context = fixture.CreateContext())
        {
            var source = await context.Ingredients.SingleAsync(
                ingredient => ingredient.Id == sourceId,
                cancellationToken);
            source.MergedIntoIngredientId.Should().Be(targetId);
        }
    }

    [Fact]
    public async Task Deleting_a_department_referenced_by_an_ingredient_is_blocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long departmentId;

        await using (var context = fixture.CreateContext())
        {
            var department = NewDepartment("M2 Restrict Produce", "m2-restrict-produce");
            context.GroceryDepartments.Add(department);
            await context.SaveChangesAsync(cancellationToken);

            var ingredient = NewIngredient("M2 Restrict Ingredient", "m2-restrict-ingredient");
            ingredient.DepartmentId = department.Id;
            context.Ingredients.Add(ingredient);
            await context.SaveChangesAsync(cancellationToken);

            departmentId = department.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var department = await context.GroceryDepartments.SingleAsync(
                candidate => candidate.Id == departmentId,
                cancellationToken);
            context.GroceryDepartments.Remove(department);

            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("fk_ingredients_grocery_departments_department_id");
        }
    }

    [Fact]
    public async Task Deleting_an_ingredient_cascades_its_aliases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long ingredientId;

        await using (var context = fixture.CreateContext())
        {
            var ingredient = NewIngredient("M2 Cascade Chicken", "m2-cascade-chicken");
            context.Ingredients.Add(ingredient);
            await context.SaveChangesAsync(cancellationToken);
            ingredientId = ingredient.Id;

            context.IngredientAliases.AddRange(
                new IngredientAlias
                {
                    IngredientId = ingredientId,
                    Alias = "M2 cascade chick",
                    NormalizedAlias = "m2-cascade-chick",
                },
                new IngredientAlias
                {
                    IngredientId = ingredientId,
                    Alias = "M2 cascade chook",
                    NormalizedAlias = "m2-cascade-chook",
                });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var ingredient = await context.Ingredients.SingleAsync(
                candidate => candidate.Id == ingredientId,
                cancellationToken);
            context.Ingredients.Remove(ingredient);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var remaining = await context.IngredientAliases
                .CountAsync(alias => alias.IngredientId == ingredientId, cancellationToken);
            remaining.Should().Be(0);
        }
    }

    [Fact]
    public async Task Deleting_a_unit_cascades_its_aliases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long unitId;

        await using (var context = fixture.CreateContext())
        {
            var unit = NewUnit("m2-cascade-cup", "M2 cascade cup", "m2-cascade-cup", UnitDimension.Volume, 236.5882365m);
            context.Units.Add(unit);
            await context.SaveChangesAsync(cancellationToken);
            unitId = unit.Id;

            context.UnitAliases.Add(new UnitAlias
            {
                UnitId = unitId,
                Alias = "M2 cascade cups",
                NormalizedAlias = "m2-cascade-cups",
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var unit = await context.Units.SingleAsync(candidate => candidate.Id == unitId, cancellationToken);
            context.Units.Remove(unit);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var remaining = await context.UnitAliases
                .CountAsync(alias => alias.UnitId == unitId, cancellationToken);
            remaining.Should().Be(0);
        }
    }

    [Fact]
    public async Task Unit_base_factor_round_trips_and_allows_null_for_count_units()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long ounceId;
        long eachId;

        await using (var context = fixture.CreateContext())
        {
            var ounce = NewUnit("m2-bf-oz", "M2 ounce factor", "m2-bf-ounce", UnitDimension.Mass, 28.349523125m);
            var each = NewUnit("m2-bf-each", "M2 each factor", "m2-bf-each", UnitDimension.Count);
            context.Units.AddRange(ounce, each);
            await context.SaveChangesAsync(cancellationToken);
            ounceId = ounce.Id;
            eachId = each.Id;
        }

        await using var verification = fixture.CreateContext();
        var reloadedOunce = await verification.Units.SingleAsync(unit => unit.Id == ounceId, cancellationToken);
        var reloadedEach = await verification.Units.SingleAsync(unit => unit.Id == eachId, cancellationToken);

        reloadedOunce.BaseFactor.Should().Be(28.349523125m);
        reloadedOunce.Dimension.Should().Be(UnitDimension.Mass);
        reloadedEach.BaseFactor.Should().BeNull();
        reloadedEach.Dimension.Should().Be(UnitDimension.Count);
    }

    [Fact]
    public async Task Audit_events_round_trip_jsonb_payloads()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long auditEventId;

        await using (var context = fixture.CreateContext())
        {
            var auditEvent = new AuditEvent
            {
                EventType = "ingredient.merge",
                EntityType = "ingredient",
                EntityId = "42",
                BeforeJson = """{"is_active": true}""",
                AfterJson = """{"merged_into_ingredient_id": 7}""",
                Reason = "duplicate ingredient",
                IpAddress = "192.0.2.10",
                CreatedAt = FixedTimestamp,
                MetadataJson = """{"source": "integration-test"}""",
            };
            context.AuditEvents.Add(auditEvent);
            await context.SaveChangesAsync(cancellationToken);
            auditEventId = auditEvent.Id;
        }

        await using var verification = fixture.CreateContext();
        var reloaded = await verification.AuditEvents.SingleAsync(
            auditEvent => auditEvent.Id == auditEventId,
            cancellationToken);

        reloaded.ActorUserId.Should().BeNull();
        reloaded.EventType.Should().Be("ingredient.merge");
        reloaded.EntityType.Should().Be("ingredient");
        reloaded.EntityId.Should().Be("42");
        reloaded.Reason.Should().Be("duplicate ingredient");
        reloaded.IpAddress.Should().Be("192.0.2.10");
        reloaded.CreatedAt.Should().Be(FixedTimestamp);

        using var before = JsonDocument.Parse(reloaded.BeforeJson!);
        before.RootElement.GetProperty("is_active").GetBoolean().Should().BeTrue();

        using var after = JsonDocument.Parse(reloaded.AfterJson!);
        after.RootElement.GetProperty("merged_into_ingredient_id").GetInt64().Should().Be(7);

        using var metadata = JsonDocument.Parse(reloaded.MetadataJson);
        metadata.RootElement.GetProperty("source").GetString().Should().Be("integration-test");
    }

    [Fact]
    public async Task Audit_events_metadata_json_defaults_to_an_empty_object()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO audit_events (event_type, entity_type, created_at)
                VALUES ('m2.metadata-default', 'test', now())
                """;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = connection.CreateCommand();
        select.CommandText =
            "SELECT metadata_json::text FROM audit_events WHERE event_type = 'm2.metadata-default'";
        var metadata = (string?)await select.ExecuteScalarAsync(cancellationToken);

        metadata.Should().NotBeNull();
        using var document = JsonDocument.Parse(metadata!);
        document.RootElement.EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public async Task Audit_events_require_event_type_and_entity_type()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        await using (var missingEventType = connection.CreateCommand())
        {
            missingEventType.CommandText =
                "INSERT INTO audit_events (entity_type, created_at) VALUES ('test', now())";
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => missingEventType.ExecuteNonQueryAsync(cancellationToken));
            exception.SqlState.Should().Be(PostgresErrorCodes.NotNullViolation);
        }

        await using var missingEntityType = connection.CreateCommand();
        missingEntityType.CommandText =
            "INSERT INTO audit_events (event_type, created_at) VALUES ('m2.not-null', now())";
        var entityTypeException = await Assert.ThrowsAsync<PostgresException>(
            () => missingEntityType.ExecuteNonQueryAsync(cancellationToken));
        entityTypeException.SqlState.Should().Be(PostgresErrorCodes.NotNullViolation);
    }

    [Fact]
    public async Task Deleting_an_audit_actor_is_blocked_by_restrict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long userId;

        await using (var context = fixture.CreateContext())
        {
            var user = NewUser("M2 Audit Actor", "m2-audit-actor");
            context.Users.Add(user);
            await context.SaveChangesAsync(cancellationToken);
            userId = user.Id;

            context.AuditEvents.Add(new AuditEvent
            {
                ActorUserId = userId,
                EventType = "unit.factor_changed",
                EntityType = "unit",
                EntityId = "1",
                CreatedAt = FixedTimestamp,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId, cancellationToken);
            context.Users.Remove(user);

            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("fk_audit_events_users_actor_user_id");
        }
    }

    private async Task AssertUniqueViolationAsync<TEntity>(TEntity first, TEntity second)
        where TEntity : class
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var context = fixture.CreateContext())
        {
            context.Add(first);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.Add(second);
            await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
        }
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
                       (is_nullable = 'YES') AS is_nullable,
                       numeric_precision,
                       numeric_scale
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

    private static GroceryDepartment NewDepartment(string name, string normalizedName, int sortOrder = 0) => new()
    {
        Name = name,
        NormalizedName = normalizedName,
        SortOrder = sortOrder,
        CreatedAt = FixedTimestamp,
        UpdatedAt = FixedTimestamp,
    };

    private static Ingredient NewIngredient(string canonicalName, string normalizedName) => new()
    {
        CanonicalName = canonicalName,
        NormalizedName = normalizedName,
        CreatedAt = FixedTimestamp,
        UpdatedAt = FixedTimestamp,
    };

    private static Unit NewUnit(
        string symbol,
        string displayName,
        string normalizedName,
        UnitDimension dimension,
        decimal? baseFactor = null) => new()
        {
            Symbol = symbol,
            DisplayName = displayName,
            NormalizedName = normalizedName,
            Dimension = dimension,
            BaseFactor = baseFactor,
        };

    private static Protein NewProtein(string name, string normalizedName) => new()
    {
        Name = name,
        NormalizedName = normalizedName,
    };

    private static Tag NewTag(string name, string normalizedName) => new()
    {
        Name = name,
        NormalizedName = normalizedName,
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

    private sealed record ExpectedColumn(string Type, bool Nullable, int? Precision = null, int? Scale = null);

    private sealed record ColumnInfo(string ColumnName, string DataType, bool IsNullable, int? NumericPrecision, int? NumericScale);

    private sealed record IndexInfo(string IndexName, string IndexDefinition);

    private sealed record TableInfo(string TableName);
}
