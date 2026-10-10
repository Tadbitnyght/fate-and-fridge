using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// PostgreSQL-backed verification of the M8-01 grocery schema
/// (docs/M8_PLAN.md §2.3; docs/DATABASE.md §9). Requires a Docker daemon; run
/// with <c>dotnet test --filter "Category!=Integration"</c> to skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M8GroceriesSchemaCollection.Name)]
public sealed class M8GroceriesSchemaIntegrationTests(M8GroceriesSchemaFixture fixture)
{
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] M8Tables =
    [
        "grocery_staples",
        "manual_grocery_items",
        "grocery_check_state",
        "user_grocery_preferences",
    ];

    [Fact]
    public async Task Migration_is_applied_from_scratch_and_no_pending_migrations_remain()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        applied.Should().Contain(migration => migration.EndsWith("M8Groceries", StringComparison.Ordinal));

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task M8_tables_expose_the_expected_columns_and_types()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = new Dictionary<string, Dictionary<string, ExpectedColumn>>
        {
            ["grocery_staples"] = new()
            {
                ["id"] = new("bigint", false),
                ["name"] = new("text", false),
                ["ingredient_id"] = new("bigint", true),
                ["department_id"] = new("bigint", true),
                ["default_quantity_numeric"] = new("numeric", true, Precision: 28, Scale: 14),
                ["unit_id"] = new("bigint", true),
                ["is_active"] = new("boolean", false),
                ["created_at"] = new("timestamp with time zone", false),
                ["updated_at"] = new("timestamp with time zone", false),
            },
            ["manual_grocery_items"] = new()
            {
                ["id"] = new("bigint", false),
                ["name"] = new("text", false),
                ["ingredient_id"] = new("bigint", true),
                ["department_id"] = new("bigint", true),
                ["quantity_numeric"] = new("numeric", true, Precision: 28, Scale: 14),
                ["unit_id"] = new("bigint", true),
                ["range_start"] = new("date", false),
                ["range_days"] = new("integer", false),
                ["save_for_future"] = new("boolean", false),
                ["created_by_user_id"] = new("bigint", true),
                ["created_at"] = new("timestamp with time zone", false),
                ["updated_at"] = new("timestamp with time zone", false),
            },
            ["grocery_check_state"] = new()
            {
                ["id"] = new("bigint", false),
                ["item_identity_hash"] = new("text", false),
                ["checked"] = new("boolean", false),
                ["checked_by_user_id"] = new("bigint", true),
                ["checked_at"] = new("timestamp with time zone", true),
                ["updated_at"] = new("timestamp with time zone", false),
            },
            ["user_grocery_preferences"] = new()
            {
                ["user_id"] = new("bigint", false),
                ["hide_checked"] = new("boolean", false),
                ["range_preset"] = new("text", true),
                ["range_start"] = new("date", true),
                ["range_days"] = new("integer", true),
                ["updated_at"] = new("timestamp with time zone", false),
            },
        };

        foreach (var (table, columns) in expected)
        {
            var actual = (await GetColumnsAsync(table, cancellationToken))
                .ToDictionary(column => column.ColumnName, StringComparer.Ordinal);

            actual.Keys.Should().BeEquivalentTo(
                columns.Keys,
                $"table {table} should contain exactly the approved M8 columns");

            foreach (var (column, expectation) in columns)
            {
                actual[column].DataType.Should().Be(expectation.Type, $"{table}.{column} should use {expectation.Type}");
                actual[column].IsNullable.Should().Be(expectation.Nullable, $"{table}.{column} nullability");

                if (expectation.Type == "numeric")
                {
                    actual[column].NumericPrecision.Should().Be(expectation.Precision, $"{table}.{column} precision");
                    actual[column].NumericScale.Should().Be(expectation.Scale, $"{table}.{column} scale");
                }
            }
        }
    }

    [Fact]
    public async Task Expected_grocery_indexes_exist_with_expected_uniqueness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var indexes = (await GetIndexesAsync(cancellationToken))
            .ToDictionary(index => index.IndexName, StringComparer.Ordinal);

        AssertIndex(indexes, "pk_grocery_staples", unique: true);
        AssertIndex(indexes, "ix_grocery_staples_ingredient_id", unique: false);
        AssertIndex(indexes, "ix_grocery_staples_department_id", unique: false);
        AssertIndex(indexes, "ix_grocery_staples_unit_id", unique: false);
        AssertIndex(indexes, "pk_manual_grocery_items", unique: true);
        AssertIndex(indexes, "ix_manual_grocery_items_range_start_range_days", unique: false);
        AssertIndex(indexes, "ix_manual_grocery_items_created_by_user_id", unique: false);
        AssertIndex(indexes, "pk_grocery_check_state", unique: true);
        AssertIndex(indexes, "ix_grocery_check_state_item_identity_hash", unique: true);
        AssertIndex(indexes, "ix_grocery_check_state_checked_by_user_id", unique: false);
        AssertIndex(indexes, "pk_user_grocery_preferences", unique: true);
    }

    [Fact]
    public async Task Grocery_check_state_identity_is_unique()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string identityHash = "m8-check-identity-unique";

        await using (var context = fixture.CreateContext())
        {
            context.GroceryCheckStates.Add(NewCheckState(identityHash));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.GroceryCheckStates.Add(NewCheckState(identityHash));
            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("ix_grocery_check_state_item_identity_hash");
        }
    }

    [Fact]
    public async Task User_grocery_preference_is_unique_per_user()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long userId;

        await using (var context = fixture.CreateContext())
        {
            var user = NewUser("M8 Pref User", "m8-pref-user");
            context.Users.Add(user);
            await context.SaveChangesAsync(cancellationToken);
            userId = user.Id;

            context.UserGroceryPreferences.Add(NewPreference(userId));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.UserGroceryPreferences.Add(NewPreference(userId));
            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("pk_user_grocery_preferences");
        }
    }

    [Fact]
    public async Task Deleting_a_user_cascades_their_grocery_preference()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long userId;

        await using (var context = fixture.CreateContext())
        {
            var user = NewUser("M8 Pref Cascade", "m8-pref-cascade");
            context.Users.Add(user);
            await context.SaveChangesAsync(cancellationToken);
            userId = user.Id;

            context.UserGroceryPreferences.Add(NewPreference(userId));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var user = await context.Users.SingleAsync(candidate => candidate.Id == userId, cancellationToken);
            context.Users.Remove(user);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        (await verification.UserGroceryPreferences
            .CountAsync(preference => preference.UserId == userId, cancellationToken))
            .Should().Be(0);
    }

    [Fact]
    public async Task Deleting_a_referenced_ingredient_is_blocked_for_a_staple()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long ingredientId;

        await using (var context = fixture.CreateContext())
        {
            var ingredient = NewIngredient("M8 Restrict Ingredient", "m8-restrict-ingredient");
            context.Ingredients.Add(ingredient);
            await context.SaveChangesAsync(cancellationToken);
            ingredientId = ingredient.Id;

            context.GroceryStaples.Add(new GroceryStaple
            {
                Name = "M8 Restrict Staple",
                IngredientId = ingredientId,
                CreatedAt = FixedTimestamp,
                UpdatedAt = FixedTimestamp,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            var ingredient = await context.Ingredients.SingleAsync(
                candidate => candidate.Id == ingredientId, cancellationToken);
            context.Ingredients.Remove(ingredient);

            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("fk_grocery_staples_ingredients_ingredient_id");
        }
    }

    private static GroceryCheckState NewCheckState(string identityHash) => new()
    {
        ItemIdentityHash = identityHash,
        Checked = true,
        CheckedAt = FixedTimestamp,
        UpdatedAt = FixedTimestamp,
    };

    private static UserGroceryPreference NewPreference(long userId) => new()
    {
        UserId = userId,
        HideChecked = true,
        RangePreset = "next_7_days",
        RangeStart = new DateOnly(2026, 10, 9),
        RangeDays = 7,
        UpdatedAt = FixedTimestamp,
    };

    private static Ingredient NewIngredient(string canonicalName, string normalizedName) => new()
    {
        CanonicalName = canonicalName,
        NormalizedName = normalizedName,
        CreatedAt = FixedTimestamp,
        UpdatedAt = FixedTimestamp,
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

    private sealed record ExpectedColumn(string Type, bool Nullable, int? Precision = null, int? Scale = null);

    private sealed record ColumnInfo(string ColumnName, string DataType, bool IsNullable, int? NumericPrecision, int? NumericScale);

    private sealed record IndexInfo(string IndexName, string IndexDefinition);
}
