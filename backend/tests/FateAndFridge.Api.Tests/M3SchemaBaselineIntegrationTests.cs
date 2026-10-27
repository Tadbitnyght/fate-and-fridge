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
/// PostgreSQL-backed verification of the M3 recipe schema baseline
/// (docs/DATABASE.md §3, §6, §7; docs/M3_PLAN.md §2). Requires a Docker
/// daemon; run with <c>dotnet test --filter "Category!=Integration"</c> to
/// skip locally.
/// </summary>
[Trait("Category", "Integration")]
[Collection(M3SchemaBaselineCollection.Name)]
public sealed class M3SchemaBaselineIntegrationTests(M3SchemaBaselineFixture fixture)
{
    private const string M2MigrationId = "20261001042238_M2ReferenceDataBaseline";

    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

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

    private static readonly string[] M3Tables =
    [
        "recipes",
        "recipe_images",
        "recipe_ingredient_groups",
        "recipe_ingredients",
        "recipe_instruction_sections",
        "recipe_instruction_steps",
        "recipe_proteins",
        "recipe_tags",
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
        applied.Should().Contain(migration => migration.EndsWith("M3RecipeSchemaBaseline", StringComparison.Ordinal));

        var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
        pending.Should().BeEmpty();
    }

    [Fact]
    public async Task M3_migration_applies_to_an_M2_baseline_database()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();

        // A database that only ran through the M2 baseline must upgrade
        // cleanly; the M3 down script must also be dependency-safe.
        await migrator.MigrateAsync(M2MigrationId, cancellationToken);

        var tablesAtM2 = await GetTableNamesAsync(context, cancellationToken);
        tablesAtM2.Should().Contain(M2Tables);
        tablesAtM2.Intersect(M3Tables).Should().BeEmpty("M3 tables must not exist before the M3 migration runs");

        await migrator.MigrateAsync(cancellationToken: cancellationToken);

        var tablesAfterUpgrade = await GetTableNamesAsync(context, cancellationToken);
        tablesAfterUpgrade.Should().Contain(M3Tables);
    }

    [Fact]
    public async Task M3_tables_expose_the_expected_columns_and_types()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = new Dictionary<string, Dictionary<string, ExpectedColumn>>
        {
            ["recipes"] = new()
            {
                ["id"] = new("bigint", false),
                ["name"] = new("text", false),
                ["normalized_name"] = new("text", false),
                ["description"] = new("text", true),
                ["source_site"] = new("text", true),
                ["source_url"] = new("text", true),
                ["normalized_source_url"] = new("text", true),
                ["author"] = new("text", true),
                ["prep_minutes"] = new("integer", true),
                ["cook_minutes"] = new("integer", true),
                ["notes"] = new("text", true),
                ["is_favorite"] = new("boolean", false),
                ["status"] = new("text", false),
                ["is_vegan"] = new("boolean", false),
                ["is_vegetarian"] = new("boolean", false),
                ["created_at"] = new("timestamp with time zone", false),
                ["updated_at"] = new("timestamp with time zone", false),
            },
            ["recipe_images"] = new()
            {
                ["id"] = new("bigint", false),
                ["recipe_id"] = new("bigint", false),
                ["storage_path"] = new("text", false),
                ["source_url"] = new("text", true),
                ["alt_text"] = new("text", true),
                ["created_at"] = new("timestamp with time zone", false),
            },
            ["recipe_ingredient_groups"] = new()
            {
                ["id"] = new("bigint", false),
                ["recipe_id"] = new("bigint", false),
                ["title"] = new("text", true),
                ["sort_order"] = new("integer", false),
            },
            ["recipe_ingredients"] = new()
            {
                ["id"] = new("bigint", false),
                ["recipe_id"] = new("bigint", false),
                ["group_id"] = new("bigint", true),
                ["ingredient_id"] = new("bigint", false),
                ["quantity_numeric"] = new("numeric", true, Precision: 28, Scale: 14),
                ["unit_id"] = new("bigint", true),
                ["preparation_note"] = new("text", true),
                ["is_optional"] = new("boolean", false),
                ["sort_order"] = new("integer", false),
            },
            ["recipe_instruction_sections"] = new()
            {
                ["id"] = new("bigint", false),
                ["recipe_id"] = new("bigint", false),
                ["title"] = new("text", true),
                ["sort_order"] = new("integer", false),
            },
            ["recipe_instruction_steps"] = new()
            {
                ["id"] = new("bigint", false),
                ["section_id"] = new("bigint", false),
                ["text"] = new("text", false),
                ["sort_order"] = new("integer", false),
            },
            ["recipe_proteins"] = new()
            {
                ["recipe_id"] = new("bigint", false),
                ["protein_id"] = new("bigint", false),
            },
            ["recipe_tags"] = new()
            {
                ["recipe_id"] = new("bigint", false),
                ["tag_id"] = new("bigint", false),
            },
        };

        foreach (var (table, columns) in expected)
        {
            var actual = (await GetColumnsAsync(table, cancellationToken))
                .ToDictionary(column => column.ColumnName, StringComparer.Ordinal);

            actual.Keys.Should().BeEquivalentTo(
                columns.Keys,
                $"table {table} should contain exactly the approved M3 columns");

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
    public async Task Expected_recipe_indexes_exist_with_expected_uniqueness()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var indexes = (await GetIndexesAsync(cancellationToken))
            .ToDictionary(index => index.IndexName, StringComparer.Ordinal);

        AssertIndex(indexes, "pk_recipes", unique: true);
        AssertIndex(indexes, "ix_recipes_normalized_name", unique: true);
        AssertIndex(indexes, "ix_recipes_normalized_source_url", unique: false);
        AssertIndex(indexes, "ix_recipe_images_recipe_id", unique: true);
        AssertIndex(indexes, "ix_recipe_ingredient_groups_recipe_id", unique: false);
        AssertIndex(indexes, "ix_recipe_ingredients_group_id", unique: false);
        AssertIndex(indexes, "ix_recipe_ingredients_ingredient_id", unique: false);
        AssertIndex(indexes, "ix_recipe_ingredients_recipe_id", unique: false);
        AssertIndex(indexes, "ix_recipe_ingredients_unit_id", unique: false);
        AssertIndex(indexes, "ix_recipe_instruction_sections_recipe_id", unique: false);
        AssertIndex(indexes, "ix_recipe_instruction_steps_section_id", unique: false);
        AssertIndex(indexes, "pk_recipe_proteins", unique: true);
        AssertIndex(indexes, "ix_recipe_proteins_protein_id", unique: false);
        AssertIndex(indexes, "pk_recipe_tags", unique: true);
        AssertIndex(indexes, "ix_recipe_tags_tag_id", unique: false);
    }

    [Fact]
    public async Task Recipe_normalized_name_is_globally_unique()
    {
        await AssertUniqueViolationAsync(
            NewRecipe("M3 Soup One", "m3-soup-one"),
            NewRecipe("M3 Soup Two", "m3-soup-one"));
    }

    [Fact]
    public async Task Recipe_normalized_source_url_is_not_unique()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using (var context = fixture.CreateContext())
        {
            var first = NewRecipe("M3 Source One", "m3-source-one");
            first.NormalizedSourceUrl = "m3-source-url";
            var second = NewRecipe("M3 Source Two", "m3-source-two");
            second.NormalizedSourceUrl = "m3-source-url";
            context.Recipes.AddRange(first, second);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        var count = await verification.Recipes
            .CountAsync(recipe => recipe.NormalizedSourceUrl == "m3-source-url", cancellationToken);
        count.Should().Be(2);
    }

    [Fact]
    public async Task Recipe_image_is_unique_per_recipe()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long recipeId;

        await using (var context = fixture.CreateContext())
        {
            var recipe = NewRecipe("M3 Image Recipe", "m3-image-recipe");
            context.Recipes.Add(recipe);
            await context.SaveChangesAsync(cancellationToken);
            recipeId = recipe.Id;

            context.RecipeImages.Add(new RecipeImage
            {
                RecipeId = recipeId,
                StoragePath = "m3/first.jpg",
                CreatedAt = FixedTimestamp,
            });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.RecipeImages.Add(new RecipeImage
            {
                RecipeId = recipeId,
                StoragePath = "m3/second.jpg",
                CreatedAt = FixedTimestamp,
            });
            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("ix_recipe_images_recipe_id");
        }
    }

    [Fact]
    public async Task Recipe_protein_and_tag_pairs_are_unique()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long recipeId;
        long proteinId;
        long tagId;

        await using (var context = fixture.CreateContext())
        {
            var recipe = NewRecipe("M3 Join Recipe", "m3-join-recipe");
            var protein = NewProtein("M3 Join Protein", "m3-join-protein");
            var tag = NewTag("M3 Join Tag", "m3-join-tag");
            context.Recipes.Add(recipe);
            context.Proteins.Add(protein);
            context.Tags.Add(tag);
            await context.SaveChangesAsync(cancellationToken);
            recipeId = recipe.Id;
            proteinId = protein.Id;
            tagId = tag.Id;

            context.RecipeProteins.Add(new RecipeProtein { RecipeId = recipeId, ProteinId = proteinId });
            context.RecipeTags.Add(new RecipeTag { RecipeId = recipeId, TagId = tagId });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = fixture.CreateContext())
        {
            context.RecipeProteins.Add(new RecipeProtein { RecipeId = recipeId, ProteinId = proteinId });
            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("pk_recipe_proteins");
        }

        await using (var context = fixture.CreateContext())
        {
            context.RecipeTags.Add(new RecipeTag { RecipeId = recipeId, TagId = tagId });
            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.UniqueViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("pk_recipe_tags");
        }
    }

    [Fact]
    public async Task Deleting_a_referenced_ingredient_or_unit_is_blocked_by_restrict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long ingredientId;
        long unitId;

        await using (var context = fixture.CreateContext())
        {
            var ingredient = NewIngredient("M3 Restrict Ingredient", "m3-restrict-ingredient");
            var unit = NewUnit("m3-restrict-unit", "M3 restrict unit", "m3-restrict-unit", UnitDimension.Count);
            context.Ingredients.Add(ingredient);
            context.Units.Add(unit);
            await context.SaveChangesAsync(cancellationToken);

            var recipe = NewRecipe("M3 Restrict Recipe", "m3-restrict-recipe");
            context.Recipes.Add(recipe);
            await context.SaveChangesAsync(cancellationToken);

            context.RecipeIngredients.Add(new RecipeIngredient
            {
                RecipeId = recipe.Id,
                IngredientId = ingredient.Id,
                UnitId = unit.Id,
                SortOrder = 0,
            });
            await context.SaveChangesAsync(cancellationToken);

            ingredientId = ingredient.Id;
            unitId = unit.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var ingredient = await context.Ingredients.SingleAsync(
                candidate => candidate.Id == ingredientId, cancellationToken);
            context.Ingredients.Remove(ingredient);

            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("fk_recipe_ingredients_ingredients_ingredient_id");
        }

        await using (var context = fixture.CreateContext())
        {
            var unit = await context.Units.SingleAsync(
                candidate => candidate.Id == unitId, cancellationToken);
            context.Units.Remove(unit);

            var exception = await AssertSaveChangesFailsWithAsync(
                PostgresErrorCodes.ForeignKeyViolation,
                () => context.SaveChangesAsync(cancellationToken));
            exception.ConstraintName.Should().Be("fk_recipe_ingredients_units_unit_id");
        }
    }

    [Fact]
    public async Task Deleting_a_recipe_cascades_all_children()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long recipeId;
        long sectionId;
        long ingredientId;
        long unitId;
        long proteinId;
        long tagId;

        await using (var context = fixture.CreateContext())
        {
            var ingredient = NewIngredient("M3 Cascade Ingredient", "m3-cascade-ingredient");
            var unit = NewUnit("m3-cascade-unit", "M3 cascade unit", "m3-cascade-unit", UnitDimension.Count);
            var protein = NewProtein("M3 Cascade Protein", "m3-cascade-protein");
            var tag = NewTag("M3 Cascade Tag", "m3-cascade-tag");
            context.Ingredients.Add(ingredient);
            context.Units.Add(unit);
            context.Proteins.Add(protein);
            context.Tags.Add(tag);
            await context.SaveChangesAsync(cancellationToken);

            var recipe = NewRecipe("M3 Cascade Recipe", "m3-cascade-recipe");
            context.Recipes.Add(recipe);
            await context.SaveChangesAsync(cancellationToken);

            var group = new RecipeIngredientGroup
            {
                RecipeId = recipe.Id,
                Title = "M3 group",
                SortOrder = 0,
            };
            var section = new RecipeInstructionSection
            {
                RecipeId = recipe.Id,
                Title = "M3 section",
                SortOrder = 0,
            };
            context.RecipeIngredientGroups.Add(group);
            context.RecipeInstructionSections.Add(section);
            await context.SaveChangesAsync(cancellationToken);

            context.RecipeImages.Add(new RecipeImage
            {
                RecipeId = recipe.Id,
                StoragePath = "m3/cascade.jpg",
                CreatedAt = FixedTimestamp,
            });
            context.RecipeIngredients.Add(new RecipeIngredient
            {
                RecipeId = recipe.Id,
                GroupId = group.Id,
                IngredientId = ingredient.Id,
                QuantityNumeric = 2m,
                UnitId = unit.Id,
                SortOrder = 0,
            });
            context.RecipeInstructionSteps.Add(new RecipeInstructionStep
            {
                SectionId = section.Id,
                Text = "M3 stir.",
                SortOrder = 0,
            });
            context.RecipeProteins.Add(new RecipeProtein { RecipeId = recipe.Id, ProteinId = protein.Id });
            context.RecipeTags.Add(new RecipeTag { RecipeId = recipe.Id, TagId = tag.Id });
            await context.SaveChangesAsync(cancellationToken);

            recipeId = recipe.Id;
            sectionId = section.Id;
            ingredientId = ingredient.Id;
            unitId = unit.Id;
            proteinId = protein.Id;
            tagId = tag.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var recipe = await context.Recipes.SingleAsync(
                candidate => candidate.Id == recipeId, cancellationToken);
            context.Recipes.Remove(recipe);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using var verification = fixture.CreateContext();
        (await verification.RecipeImages.CountAsync(image => image.RecipeId == recipeId, cancellationToken))
            .Should().Be(0);
        (await verification.RecipeIngredientGroups.CountAsync(group => group.RecipeId == recipeId, cancellationToken))
            .Should().Be(0);
        (await verification.RecipeIngredients.CountAsync(line => line.RecipeId == recipeId, cancellationToken))
            .Should().Be(0);
        (await verification.RecipeInstructionSections.CountAsync(section => section.RecipeId == recipeId, cancellationToken))
            .Should().Be(0);
        (await verification.RecipeInstructionSteps.CountAsync(step => step.SectionId == sectionId, cancellationToken))
            .Should().Be(0);
        (await verification.RecipeProteins.CountAsync(link => link.RecipeId == recipeId, cancellationToken))
            .Should().Be(0);
        (await verification.RecipeTags.CountAsync(link => link.RecipeId == recipeId, cancellationToken))
            .Should().Be(0);

        // Reference data survives the recipe cascade.
        (await verification.Ingredients.CountAsync(candidate => candidate.Id == ingredientId, cancellationToken))
            .Should().Be(1);
        (await verification.Units.CountAsync(candidate => candidate.Id == unitId, cancellationToken))
            .Should().Be(1);
        (await verification.Proteins.CountAsync(candidate => candidate.Id == proteinId, cancellationToken))
            .Should().Be(1);
        (await verification.Tags.CountAsync(candidate => candidate.Id == tagId, cancellationToken))
            .Should().Be(1);
    }

    [Fact]
    public async Task Quantity_numeric_round_trips_numeric_28_14_and_allows_null()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // Fourteen integer digits and fourteen fractional digits: the full
        // numeric(28,14) range.
        const decimal quantity = 12345678901234.12345678901234m;

        long preciseLineId;
        long toTasteLineId;

        await using (var context = fixture.CreateContext())
        {
            var ingredient = NewIngredient("M3 Quantity Ingredient", "m3-quantity-ingredient");
            context.Ingredients.Add(ingredient);
            await context.SaveChangesAsync(cancellationToken);

            var recipe = NewRecipe("M3 Quantity Recipe", "m3-quantity-recipe");
            context.Recipes.Add(recipe);
            await context.SaveChangesAsync(cancellationToken);

            var precise = new RecipeIngredient
            {
                RecipeId = recipe.Id,
                IngredientId = ingredient.Id,
                QuantityNumeric = quantity,
                SortOrder = 0,
            };
            var toTaste = new RecipeIngredient
            {
                RecipeId = recipe.Id,
                IngredientId = ingredient.Id,
                QuantityNumeric = null,
                PreparationNote = "to taste",
                SortOrder = 1,
            };
            context.RecipeIngredients.AddRange(precise, toTaste);
            await context.SaveChangesAsync(cancellationToken);
            preciseLineId = precise.Id;
            toTasteLineId = toTaste.Id;
        }

        await using var verification = fixture.CreateContext();
        var reloadedPrecise = await verification.RecipeIngredients
            .SingleAsync(line => line.Id == preciseLineId, cancellationToken);
        var reloadedToTaste = await verification.RecipeIngredients
            .SingleAsync(line => line.Id == toTasteLineId, cancellationToken);

        reloadedPrecise.QuantityNumeric.Should().Be(quantity);
        reloadedPrecise.PreparationNote.Should().BeNull();
        reloadedToTaste.QuantityNumeric.Should().BeNull();
        reloadedToTaste.PreparationNote.Should().Be("to taste");
    }

    [Fact]
    public async Task Recipe_store_defaults_match_the_approved_schema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long ingredientId;

        await using (var seed = fixture.CreateContext())
        {
            var ingredient = NewIngredient("M3 Defaults Ingredient", "m3-defaults-ingredient");
            seed.Ingredients.Add(ingredient);
            await seed.SaveChangesAsync(cancellationToken);
            ingredientId = ingredient.Id;
        }

        await using var context = fixture.CreateContext();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);

        long recipeId;
        await using (var insertRecipe = connection.CreateCommand())
        {
            insertRecipe.CommandText =
                """
                INSERT INTO recipes (name, normalized_name, created_at, updated_at)
                VALUES ('M3 Defaults Recipe', 'm3-defaults-recipe', now(), now())
                RETURNING id
                """;
            recipeId = (long)(await insertRecipe.ExecuteScalarAsync(cancellationToken))!;
        }

        long lineId;
        await using (var insertLine = connection.CreateCommand())
        {
            insertLine.CommandText =
                """
                INSERT INTO recipe_ingredients (recipe_id, ingredient_id, sort_order)
                VALUES (@recipe_id, @ingredient_id, 0)
                RETURNING id
                """;
            insertLine.Parameters.Add(new NpgsqlParameter("recipe_id", recipeId));
            insertLine.Parameters.Add(new NpgsqlParameter("ingredient_id", ingredientId));
            lineId = (long)(await insertLine.ExecuteScalarAsync(cancellationToken))!;
        }

        var recipeState = await context.Database
            .SqlQueryRaw<RecipeDefaultState>(
                """
                SELECT status, is_favorite, is_vegan, is_vegetarian
                FROM recipes
                WHERE id = {0}
                """,
                recipeId)
            .SingleAsync(cancellationToken);
        recipeState.Status.Should().Be("normal");
        recipeState.IsFavorite.Should().BeFalse();
        recipeState.IsVegan.Should().BeFalse();
        recipeState.IsVegetarian.Should().BeFalse();

        var lineState = await context.Database
            .SqlQueryRaw<RecipeIngredientDefaultState>(
                """
                SELECT is_optional
                FROM recipe_ingredients
                WHERE id = {0}
                """,
                lineId)
            .SingleAsync(cancellationToken);
        lineState.IsOptional.Should().BeFalse();
    }

    [Fact]
    public async Task Recipe_status_round_trips_as_snake_case_text()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        long normalId;
        long manualOnlyId;
        long retiredId;

        await using (var seedContext = fixture.CreateContext())
        {
            var normal = NewRecipe("M3 Status Normal", "m3-status-normal");
            var manualOnly = NewRecipe("M3 Status Manual Only", "m3-status-manual-only");
            manualOnly.Status = RecipeStatus.ManualOnly;
            var retired = NewRecipe("M3 Status Retired", "m3-status-retired");
            retired.Status = RecipeStatus.Retired;
            seedContext.Recipes.AddRange(normal, manualOnly, retired);
            await seedContext.SaveChangesAsync(cancellationToken);
            normalId = normal.Id;
            manualOnlyId = manualOnly.Id;
            retiredId = retired.Id;
        }

        await using var context = fixture.CreateContext();
        var normalText = await context.Database
            .SqlQueryRaw<RecipeStatusState>(
                "SELECT status FROM recipes WHERE id = {0}",
                normalId)
            .SingleAsync(cancellationToken);
        var manualOnlyText = await context.Database
            .SqlQueryRaw<RecipeStatusState>(
                "SELECT status FROM recipes WHERE id = {0}",
                manualOnlyId)
            .SingleAsync(cancellationToken);
        var retiredText = await context.Database
            .SqlQueryRaw<RecipeStatusState>(
                "SELECT status FROM recipes WHERE id = {0}",
                retiredId)
            .SingleAsync(cancellationToken);

        normalText.Status.Should().Be("normal");
        manualOnlyText.Status.Should().Be("manual_only");
        retiredText.Status.Should().Be("retired");

        var reloaded = await context.Recipes
            .Where(recipe => recipe.Id == normalId || recipe.Id == manualOnlyId || recipe.Id == retiredId)
            .ToDictionaryAsync(recipe => recipe.Id, cancellationToken);

        reloaded[normalId].Status.Should().Be(RecipeStatus.Normal);
        reloaded[manualOnlyId].Status.Should().Be(RecipeStatus.ManualOnly);
        reloaded[retiredId].Status.Should().Be(RecipeStatus.Retired);
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

    private static Recipe NewRecipe(string name, string normalizedName) => new()
    {
        Name = name,
        NormalizedName = normalizedName,
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

    private sealed record ExpectedColumn(string Type, bool Nullable, int? Precision = null, int? Scale = null);

    private sealed record RecipeDefaultState(string Status, bool IsFavorite, bool IsVegan, bool IsVegetarian);

    private sealed record RecipeIngredientDefaultState(bool IsOptional);

    private sealed record RecipeStatusState(string Status);

    private sealed record ColumnInfo(string ColumnName, string DataType, bool IsNullable, int? NumericPrecision, int? NumericScale);

    private sealed record IndexInfo(string IndexName, string IndexDefinition);

    private sealed record TableInfo(string TableName);
}
