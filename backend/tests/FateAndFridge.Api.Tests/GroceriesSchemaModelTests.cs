using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Model-level checks for the grocery schema that need no database: snake_case mapping, column types
/// store defaults, the unique check-state identity, the one-row-per-user
/// preference key, and foreign-key delete behavior.
/// </summary>
public sealed class GroceriesSchemaModelTests
{
    [Fact]
    public void Grocery_entities_are_mapped()
    {
        using var context = CreateContext();

        context.Model.GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Should().Contain(
            [
                typeof(GroceryStaple),
                typeof(ManualGroceryItem),
                typeof(GroceryCheckState),
                typeof(UserGroceryPreference),
            ]);
    }

    [Fact]
    public void Grocery_tables_and_columns_use_snake_case_names()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertTableName<GroceryStaple>(model, "grocery_staples");
        AssertTableName<ManualGroceryItem>(model, "manual_grocery_items");
        // docs/DATABASE.md §9 names this table in the
        // singular, unlike the other grocery tables.
        AssertTableName<GroceryCheckState>(model, "grocery_check_state");
        AssertTableName<UserGroceryPreference>(model, "user_grocery_preferences");

        var staple = model.FindEntityType(typeof(GroceryStaple))!;
        AssertColumnName(staple, nameof(GroceryStaple.IngredientId), "ingredient_id");
        AssertColumnName(staple, nameof(GroceryStaple.DepartmentId), "department_id");
        AssertColumnName(staple, nameof(GroceryStaple.DefaultQuantityNumeric), "default_quantity_numeric");
        AssertColumnName(staple, nameof(GroceryStaple.UnitId), "unit_id");
        AssertColumnName(staple, nameof(GroceryStaple.IsActive), "is_active");

        var manual = model.FindEntityType(typeof(ManualGroceryItem))!;
        AssertColumnName(manual, nameof(ManualGroceryItem.QuantityNumeric), "quantity_numeric");
        AssertColumnName(manual, nameof(ManualGroceryItem.RangeStart), "range_start");
        AssertColumnName(manual, nameof(ManualGroceryItem.RangeDays), "range_days");
        AssertColumnName(manual, nameof(ManualGroceryItem.SaveForFuture), "save_for_future");
        AssertColumnName(manual, nameof(ManualGroceryItem.CreatedByUserId), "created_by_user_id");

        var checkState = model.FindEntityType(typeof(GroceryCheckState))!;
        AssertColumnName(checkState, nameof(GroceryCheckState.ItemIdentityHash), "item_identity_hash");
        AssertColumnName(checkState, nameof(GroceryCheckState.CheckedByUserId), "checked_by_user_id");
        AssertColumnName(checkState, nameof(GroceryCheckState.CheckedAt), "checked_at");
        AssertColumnName(checkState, nameof(GroceryCheckState.UpdatedAt), "updated_at");

        var preference = model.FindEntityType(typeof(UserGroceryPreference))!;
        AssertColumnName(preference, nameof(UserGroceryPreference.UserId), "user_id");
        AssertColumnName(preference, nameof(UserGroceryPreference.HideChecked), "hide_checked");
        AssertColumnName(preference, nameof(UserGroceryPreference.RangePreset), "range_preset");
        AssertColumnName(preference, nameof(UserGroceryPreference.RangeStart), "range_start");
        AssertColumnName(preference, nameof(UserGroceryPreference.RangeDays), "range_days");
    }

    [Fact]
    public void Grocery_columns_use_the_approved_store_types()
    {
        using var context = CreateContext();
        var model = context.Model;

        var staple = model.FindEntityType(typeof(GroceryStaple))!;
        AssertColumnType(staple, nameof(GroceryStaple.DefaultQuantityNumeric), "numeric(28,14)");
        AssertColumnType(staple, nameof(GroceryStaple.IsActive), "boolean");
        AssertColumnType(staple, nameof(GroceryStaple.CreatedAt), "timestamp with time zone");
        AssertColumnType(staple, nameof(GroceryStaple.UpdatedAt), "timestamp with time zone");

        var manual = model.FindEntityType(typeof(ManualGroceryItem))!;
        AssertColumnType(manual, nameof(ManualGroceryItem.QuantityNumeric), "numeric(28,14)");
        AssertColumnType(manual, nameof(ManualGroceryItem.RangeStart), "date");
        AssertColumnType(manual, nameof(ManualGroceryItem.RangeDays), "integer");
        AssertColumnType(manual, nameof(ManualGroceryItem.SaveForFuture), "boolean");

        var checkState = model.FindEntityType(typeof(GroceryCheckState))!;
        AssertColumnType(checkState, nameof(GroceryCheckState.ItemIdentityHash), "text");
        AssertColumnType(checkState, nameof(GroceryCheckState.Checked), "boolean");
        AssertColumnType(checkState, nameof(GroceryCheckState.CheckedAt), "timestamp with time zone");

        var preference = model.FindEntityType(typeof(UserGroceryPreference))!;
        AssertColumnType(preference, nameof(UserGroceryPreference.HideChecked), "boolean");
        AssertColumnType(preference, nameof(UserGroceryPreference.RangePreset), "text");
        AssertColumnType(preference, nameof(UserGroceryPreference.RangeStart), "date");
        AssertColumnType(preference, nameof(UserGroceryPreference.RangeDays), "integer");
    }

    [Fact]
    public void Grocery_unique_indexes_are_configured()
    {
        using var context = CreateContext();
        var model = context.Model;

        // One household-shared check row per stable item identity.
        AssertUniqueIndex(
            model.FindEntityType(typeof(GroceryCheckState))!,
            nameof(GroceryCheckState.ItemIdentityHash));

        // The owner is the primary key, so there is exactly one preference row
        // per user.
        AssertPrimaryKey<UserGroceryPreference>(model, nameof(UserGroceryPreference.UserId));
    }

    [Fact]
    public void Grocery_foreign_keys_use_the_approved_delete_behavior()
    {
        using var context = CreateContext();
        var model = context.Model;

        // Reference data is retired rather than hard-deleted.
        AssertForeignKey<GroceryStaple>(
            model, nameof(GroceryStaple.IngredientId), typeof(Ingredient), DeleteBehavior.Restrict);
        AssertForeignKey<GroceryStaple>(
            model, nameof(GroceryStaple.DepartmentId), typeof(GroceryDepartment), DeleteBehavior.Restrict);
        AssertForeignKey<GroceryStaple>(
            model, nameof(GroceryStaple.UnitId), typeof(Unit), DeleteBehavior.Restrict);

        AssertForeignKey<ManualGroceryItem>(
            model, nameof(ManualGroceryItem.IngredientId), typeof(Ingredient), DeleteBehavior.Restrict);
        AssertForeignKey<ManualGroceryItem>(
            model, nameof(ManualGroceryItem.DepartmentId), typeof(GroceryDepartment), DeleteBehavior.Restrict);
        AssertForeignKey<ManualGroceryItem>(
            model, nameof(ManualGroceryItem.UnitId), typeof(Unit), DeleteBehavior.Restrict);
        AssertForeignKey<ManualGroceryItem>(
            model, nameof(ManualGroceryItem.CreatedByUserId), typeof(User), DeleteBehavior.Restrict);

        AssertForeignKey<GroceryCheckState>(
            model, nameof(GroceryCheckState.CheckedByUserId), typeof(User), DeleteBehavior.Restrict);

        // Per-user preferences are user-owned.
        AssertForeignKey<UserGroceryPreference>(
            model, nameof(UserGroceryPreference.UserId), typeof(User), DeleteBehavior.Cascade);
    }

    [Fact]
    public void Grocery_store_defaults_match_the_approved_schema()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertDefaultValue(
            model.FindEntityType(typeof(GroceryStaple))!,
            nameof(GroceryStaple.IsActive),
            true);
        AssertDefaultValue(
            model.FindEntityType(typeof(ManualGroceryItem))!,
            nameof(ManualGroceryItem.SaveForFuture),
            false);
        AssertDefaultValue(
            model.FindEntityType(typeof(GroceryCheckState))!,
            nameof(GroceryCheckState.Checked),
            false);
        AssertDefaultValue(
            model.FindEntityType(typeof(UserGroceryPreference))!,
            nameof(UserGroceryPreference.HideChecked),
            false);
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

    private static void AssertDefaultValue(IEntityType entityType, string propertyName, object? expected) =>
        entityType.GetProperty(propertyName).GetDefaultValue().Should().Be(expected);

    private static void AssertUniqueIndex(IEntityType entityType, params string[] propertyNames) =>
        entityType.GetIndexes()
            .Where(index => index.Properties.Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(propertyNames.OrderBy(name => name, StringComparer.Ordinal)))
            .Should().ContainSingle()
            .Which.IsUnique.Should().BeTrue();

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
