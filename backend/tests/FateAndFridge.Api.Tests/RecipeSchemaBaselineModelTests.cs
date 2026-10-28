using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Model-level checks for the M3 recipe schema baseline that need no database: snake_case mapping, the
/// recipe status text converter, column types, store defaults, unique indexes
/// and foreign-key delete behavior.
/// </summary>
public sealed class RecipeSchemaBaselineModelTests
{
    [Fact]
    public void Recipe_entities_are_mapped()
    {
        using var context = CreateContext();

        context.Model.GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Should().Contain(
            [
                typeof(Recipe),
                typeof(RecipeImage),
                typeof(RecipeIngredientGroup),
                typeof(RecipeIngredient),
                typeof(RecipeInstructionSection),
                typeof(RecipeInstructionStep),
                typeof(RecipeProtein),
                typeof(RecipeTag),
            ]);
    }

    [Fact]
    public void Recipe_tables_and_columns_use_snake_case_names()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertTableName<Recipe>(model, "recipes");
        AssertTableName<RecipeImage>(model, "recipe_images");
        AssertTableName<RecipeIngredientGroup>(model, "recipe_ingredient_groups");
        AssertTableName<RecipeIngredient>(model, "recipe_ingredients");
        AssertTableName<RecipeInstructionSection>(model, "recipe_instruction_sections");
        AssertTableName<RecipeInstructionStep>(model, "recipe_instruction_steps");
        AssertTableName<RecipeProtein>(model, "recipe_proteins");
        AssertTableName<RecipeTag>(model, "recipe_tags");

        var recipe = model.FindEntityType(typeof(Recipe))!;
        AssertColumnName(recipe, nameof(Recipe.NormalizedName), "normalized_name");
        AssertColumnName(recipe, nameof(Recipe.NormalizedSourceUrl), "normalized_source_url");
        AssertColumnName(recipe, nameof(Recipe.PrepMinutes), "prep_minutes");
        AssertColumnName(recipe, nameof(Recipe.CookMinutes), "cook_minutes");
        AssertColumnName(recipe, nameof(Recipe.IsFavorite), "is_favorite");
        AssertColumnName(recipe, nameof(Recipe.IsVegan), "is_vegan");
        AssertColumnName(recipe, nameof(Recipe.IsVegetarian), "is_vegetarian");

        var image = model.FindEntityType(typeof(RecipeImage))!;
        AssertColumnName(image, nameof(RecipeImage.RecipeId), "recipe_id");
        AssertColumnName(image, nameof(RecipeImage.StoragePath), "storage_path");
        AssertColumnName(image, nameof(RecipeImage.AltText), "alt_text");

        var recipeIngredient = model.FindEntityType(typeof(RecipeIngredient))!;
        AssertColumnName(recipeIngredient, nameof(RecipeIngredient.GroupId), "group_id");
        AssertColumnName(recipeIngredient, nameof(RecipeIngredient.QuantityNumeric), "quantity_numeric");
        AssertColumnName(recipeIngredient, nameof(RecipeIngredient.PreparationNote), "preparation_note");
        AssertColumnName(recipeIngredient, nameof(RecipeIngredient.IsOptional), "is_optional");
        AssertColumnName(recipeIngredient, nameof(RecipeIngredient.SortOrder), "sort_order");

        var step = model.FindEntityType(typeof(RecipeInstructionStep))!;
        AssertColumnName(step, nameof(RecipeInstructionStep.SectionId), "section_id");
        AssertColumnName(step, nameof(RecipeInstructionStep.Text), "text");
    }

    [Fact]
    public void Recipe_columns_use_the_approved_store_types()
    {
        using var context = CreateContext();
        var model = context.Model;

        var recipe = model.FindEntityType(typeof(Recipe))!;
        AssertColumnType(recipe, nameof(Recipe.Status), "text");
        AssertColumnType(recipe, nameof(Recipe.IsFavorite), "boolean");
        AssertColumnType(recipe, nameof(Recipe.PrepMinutes), "integer");
        AssertColumnType(recipe, nameof(Recipe.CreatedAt), "timestamp with time zone");

        var image = model.FindEntityType(typeof(RecipeImage))!;
        AssertColumnType(image, nameof(RecipeImage.CreatedAt), "timestamp with time zone");

        var recipeIngredient = model.FindEntityType(typeof(RecipeIngredient))!;
        AssertColumnType(recipeIngredient, nameof(RecipeIngredient.QuantityNumeric), "numeric(28,14)");
        AssertColumnType(recipeIngredient, nameof(RecipeIngredient.IsOptional), "boolean");
        AssertColumnType(recipeIngredient, nameof(RecipeIngredient.SortOrder), "integer");

        var step = model.FindEntityType(typeof(RecipeInstructionStep))!;
        AssertColumnType(step, nameof(RecipeInstructionStep.Text), "text");
        AssertColumnType(step, nameof(RecipeInstructionStep.SortOrder), "integer");
    }

    [Fact]
    public void Recipe_status_converts_to_snake_case_text()
    {
        using var context = CreateContext();
        var status = context.Model
            .FindEntityType(typeof(Recipe))!
            .GetProperty(nameof(Recipe.Status));

        status.GetColumnType().Should().Be("text");

        var converter = status.GetValueConverter();
        converter.Should().NotBeNull();
        converter!.ConvertToProvider(RecipeStatus.Normal).Should().Be("normal");
        converter.ConvertToProvider(RecipeStatus.ManualOnly).Should().Be("manual_only");
        converter.ConvertToProvider(RecipeStatus.Retired).Should().Be("retired");
        converter.ConvertFromProvider("normal").Should().Be(RecipeStatus.Normal);
        converter.ConvertFromProvider("manual_only").Should().Be(RecipeStatus.ManualOnly);
        converter.ConvertFromProvider("retired").Should().Be(RecipeStatus.Retired);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => converter.ConvertFromProvider("bogus"));
        exception.Message.Should().Contain("Unknown");
    }

    [Fact]
    public void Recipe_unique_and_lookup_indexes_are_configured()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertUniqueIndex(model.FindEntityType(typeof(Recipe))!, nameof(Recipe.NormalizedName));
        AssertNonUniqueIndex(model.FindEntityType(typeof(Recipe))!, nameof(Recipe.NormalizedSourceUrl));

        // One active image per recipe.
        AssertUniqueIndex(model.FindEntityType(typeof(RecipeImage))!, nameof(RecipeImage.RecipeId));

        // The recipe/reference pair is the primary key on both join tables, so
        // duplicate links are impossible.
        AssertPrimaryKey<RecipeProtein>(model, nameof(RecipeProtein.RecipeId), nameof(RecipeProtein.ProteinId));
        AssertPrimaryKey<RecipeTag>(model, nameof(RecipeTag.RecipeId), nameof(RecipeTag.TagId));
    }

    [Fact]
    public void Recipe_foreign_keys_use_the_approved_delete_behavior()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertForeignKey<RecipeImage>(model, nameof(RecipeImage.RecipeId), typeof(Recipe), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeIngredientGroup>(
            model, nameof(RecipeIngredientGroup.RecipeId), typeof(Recipe), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeInstructionSection>(
            model, nameof(RecipeInstructionSection.RecipeId), typeof(Recipe), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeIngredient>(
            model, nameof(RecipeIngredient.RecipeId), typeof(Recipe), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeIngredient>(
            model, nameof(RecipeIngredient.GroupId), typeof(RecipeIngredientGroup), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeIngredient>(
            model, nameof(RecipeIngredient.IngredientId), typeof(Ingredient), DeleteBehavior.Restrict);
        AssertForeignKey<RecipeIngredient>(
            model, nameof(RecipeIngredient.UnitId), typeof(Unit), DeleteBehavior.Restrict);
        AssertForeignKey<RecipeInstructionStep>(
            model, nameof(RecipeInstructionStep.SectionId), typeof(RecipeInstructionSection), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeProtein>(
            model, nameof(RecipeProtein.RecipeId), typeof(Recipe), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeProtein>(
            model, nameof(RecipeProtein.ProteinId), typeof(Protein), DeleteBehavior.Restrict);
        AssertForeignKey<RecipeTag>(
            model, nameof(RecipeTag.RecipeId), typeof(Recipe), DeleteBehavior.Cascade);
        AssertForeignKey<RecipeTag>(
            model, nameof(RecipeTag.TagId), typeof(Tag), DeleteBehavior.Restrict);
    }

    [Fact]
    public void Recipe_store_defaults_match_the_approved_schema()
    {
        using var context = CreateContext();
        var model = context.Model;

        var recipe = model.FindEntityType(typeof(Recipe))!;
        AssertDefaultValue(recipe, nameof(Recipe.IsFavorite), false);
        AssertDefaultValue(recipe, nameof(Recipe.Status), RecipeStatus.Normal);
        AssertDefaultValue(recipe, nameof(Recipe.IsVegan), false);
        AssertDefaultValue(recipe, nameof(Recipe.IsVegetarian), false);

        var recipeIngredient = model.FindEntityType(typeof(RecipeIngredient))!;
        AssertDefaultValue(recipeIngredient, nameof(RecipeIngredient.IsOptional), false);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ApiFactory.DummyConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static void AssertTableName<TEntity>(IModel model, string expectedTableName) =>
        model.FindEntityType(typeof(TEntity))!.GetTableName().Should().Be(expectedTableName);

    private static void AssertColumnName(IEntityType entityType, string propertyName, string expectedColumnName) =>
        entityType.GetProperty(propertyName).GetColumnName().Should().Be(expectedColumnName);

    private static void AssertColumnType(IEntityType entityType, string propertyName, string expectedColumnType) =>
        entityType.GetProperty(propertyName).GetColumnType().Should().Be(expectedColumnType);

    private static void AssertDefaultValue(IEntityType entityType, string propertyName, object? expected)
    {
        var property = entityType.GetProperty(propertyName);
        property.GetDefaultValue().Should().Be(expected);
    }

    private static void AssertUniqueIndex(IEntityType entityType, params string[] propertyNames) =>
        AssertIndex(entityType, isUnique: true, propertyNames);

    private static void AssertNonUniqueIndex(IEntityType entityType, params string[] propertyNames) =>
        AssertIndex(entityType, isUnique: false, propertyNames);

    private static void AssertIndex(IEntityType entityType, bool isUnique, params string[] propertyNames) =>
        entityType.GetIndexes()
            .Where(index => index.Properties.Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(propertyNames.OrderBy(name => name, StringComparer.Ordinal)))
            .Should().ContainSingle()
            .Which.IsUnique.Should().Be(isUnique);

    private static void AssertPrimaryKey<TEntity>(IModel model, params string[] propertyNames)
    {
        var primaryKey = model.FindEntityType(typeof(TEntity))!.FindPrimaryKey();
        primaryKey.Should().NotBeNull();
        primaryKey!.Properties.Select(property => property.Name).Should().BeEquivalentTo(propertyNames);
    }

    private static void AssertForeignKey<TEntity>(
        IModel model,
        string foreignKeyPropertyName,
        Type principalType,
        DeleteBehavior expected)
    {
        var foreignKey = model.FindEntityType(typeof(TEntity))!
            .GetForeignKeys()
            .Where(candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual([foreignKeyPropertyName]))
            .Should().ContainSingle(
                $"{typeof(TEntity).Name}.{foreignKeyPropertyName} should map to one foreign key")
            .Which;

        foreignKey.PrincipalEntityType.ClrType.Should().Be(principalType);
        foreignKey.DeleteBehavior.Should().Be(expected);
    }
}
