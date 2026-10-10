using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Model-level checks for the M2 reference-data and audit schema baseline
/// (docs/DATABASE.md §4, §5, §7, §10) that need no database: snake_case
/// mapping, the unit-dimension text converter, column types, store defaults,
/// unique indexes, and foreign-key delete behavior.
/// </summary>
public sealed class M2SchemaBaselineModelTests
{
    [Fact]
    public void All_expected_entities_are_mapped()
    {
        using var context = CreateContext();

        context.Model.GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Should().BeEquivalentTo(
            [
                typeof(HouseholdSettings),
                typeof(ApplicationSetting),
                typeof(User),
                typeof(UserPermission),
                typeof(AuthEvent),
                typeof(UserSession),
                typeof(GroceryDepartment),
                typeof(Ingredient),
                typeof(IngredientAlias),
                typeof(Unit),
                typeof(UnitAlias),
                typeof(Protein),
                typeof(Tag),
                typeof(AuditEvent),
                typeof(Recipe),
                typeof(RecipeImage),
                typeof(RecipeIngredientGroup),
                typeof(RecipeIngredient),
                typeof(RecipeInstructionSection),
                typeof(RecipeInstructionStep),
                typeof(RecipeProtein),
                typeof(RecipeTag),
                typeof(CalendarEntry),
                typeof(CalendarEntryHistory),
                typeof(CalendarRerollDraft),
                typeof(GroceryStaple),
                typeof(ManualGroceryItem),
                typeof(GroceryCheckState),
                typeof(UserGroceryPreference),
            ]);
    }

    [Fact]
    public void Tables_and_columns_use_snake_case_names()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertTableName<GroceryDepartment>(model, "grocery_departments");
        AssertTableName<Ingredient>(model, "ingredients");
        AssertTableName<IngredientAlias>(model, "ingredient_aliases");
        AssertTableName<Unit>(model, "units");
        AssertTableName<UnitAlias>(model, "unit_aliases");
        AssertTableName<Protein>(model, "proteins");
        AssertTableName<Tag>(model, "tags");
        AssertTableName<AuditEvent>(model, "audit_events");

        var ingredient = model.FindEntityType(typeof(Ingredient))!;
        AssertColumnName(ingredient, nameof(Ingredient.CanonicalName), "canonical_name");
        AssertColumnName(ingredient, nameof(Ingredient.NormalizedName), "normalized_name");
        AssertColumnName(ingredient, nameof(Ingredient.DepartmentId), "department_id");
        AssertColumnName(ingredient, nameof(Ingredient.MergedIntoIngredientId), "merged_into_ingredient_id");

        var unit = model.FindEntityType(typeof(Unit))!;
        AssertColumnName(unit, nameof(Unit.DisplayName), "display_name");
        AssertColumnName(unit, nameof(Unit.NormalizedName), "normalized_name");
        AssertColumnName(unit, nameof(Unit.BaseFactor), "base_factor");
        AssertColumnName(unit, nameof(Unit.IsCustom), "is_custom");

        var auditEvent = model.FindEntityType(typeof(AuditEvent))!;
        AssertColumnName(auditEvent, nameof(AuditEvent.ActorUserId), "actor_user_id");
        AssertColumnName(auditEvent, nameof(AuditEvent.EntityId), "entity_id");
        AssertColumnName(auditEvent, nameof(AuditEvent.BeforeJson), "before_json");
        AssertColumnName(auditEvent, nameof(AuditEvent.AfterJson), "after_json");
        AssertColumnName(auditEvent, nameof(AuditEvent.MetadataJson), "metadata_json");
    }

    [Fact]
    public void Instant_numeric_and_json_columns_use_the_approved_store_types()
    {
        using var context = CreateContext();
        var model = context.Model;

        var department = model.FindEntityType(typeof(GroceryDepartment))!;
        AssertColumnType(department, nameof(GroceryDepartment.SortOrder), "integer");
        AssertColumnType(department, nameof(GroceryDepartment.CreatedAt), "timestamp with time zone");
        AssertColumnType(department, nameof(GroceryDepartment.UpdatedAt), "timestamp with time zone");

        var ingredient = model.FindEntityType(typeof(Ingredient))!;
        AssertColumnType(ingredient, nameof(Ingredient.IsActive), "boolean");
        AssertColumnType(ingredient, nameof(Ingredient.CreatedAt), "timestamp with time zone");

        var unit = model.FindEntityType(typeof(Unit))!;
        AssertColumnType(unit, nameof(Unit.BaseFactor), "numeric(28,14)");
        AssertColumnType(unit, nameof(Unit.Dimension), "text");
        AssertColumnType(unit, nameof(Unit.IsCustom), "boolean");

        var auditEvent = model.FindEntityType(typeof(AuditEvent))!;
        AssertColumnType(auditEvent, nameof(AuditEvent.CreatedAt), "timestamp with time zone");
        AssertColumnType(auditEvent, nameof(AuditEvent.BeforeJson), "jsonb");
        AssertColumnType(auditEvent, nameof(AuditEvent.AfterJson), "jsonb");
        AssertColumnType(auditEvent, nameof(AuditEvent.MetadataJson), "jsonb");
        AssertColumnType(auditEvent, nameof(AuditEvent.EntityId), "text");
    }

    [Fact]
    public void Unit_dimension_converts_to_snake_case_text()
    {
        using var context = CreateContext();
        var dimension = context.Model
            .FindEntityType(typeof(Unit))!
            .GetProperty(nameof(Unit.Dimension));

        dimension.GetColumnType().Should().Be("text");

        var converter = dimension.GetValueConverter();
        converter.Should().NotBeNull();
        converter!.ConvertToProvider(UnitDimension.Mass).Should().Be("mass");
        converter.ConvertToProvider(UnitDimension.Volume).Should().Be("volume");
        converter.ConvertToProvider(UnitDimension.Count).Should().Be("count");
        converter.ConvertToProvider(UnitDimension.Nonconvertible).Should().Be("nonconvertible");
        converter.ConvertFromProvider("mass").Should().Be(UnitDimension.Mass);
        converter.ConvertFromProvider("count").Should().Be(UnitDimension.Count);
        converter.ConvertFromProvider("nonconvertible").Should().Be(UnitDimension.Nonconvertible);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => converter.ConvertFromProvider("bogus"));
        exception.Message.Should().Contain("Unknown");
    }

    [Fact]
    public void Required_unique_indexes_are_configured()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertUniqueIndex(model.FindEntityType(typeof(GroceryDepartment))!, nameof(GroceryDepartment.NormalizedName));
        AssertUniqueIndex(model.FindEntityType(typeof(Ingredient))!, nameof(Ingredient.NormalizedName));
        AssertUniqueIndex(model.FindEntityType(typeof(IngredientAlias))!, nameof(IngredientAlias.NormalizedAlias));
        AssertUniqueIndex(model.FindEntityType(typeof(Unit))!, nameof(Unit.Symbol));
        AssertUniqueIndex(model.FindEntityType(typeof(Unit))!, nameof(Unit.NormalizedName));
        AssertUniqueIndex(
            model.FindEntityType(typeof(UnitAlias))!,
            nameof(UnitAlias.UnitId),
            nameof(UnitAlias.NormalizedAlias));
        AssertUniqueIndex(model.FindEntityType(typeof(Protein))!, nameof(Protein.NormalizedName));
        AssertUniqueIndex(model.FindEntityType(typeof(Tag))!, nameof(Tag.NormalizedName));
    }

    [Fact]
    public void Audit_event_indexes_are_configured()
    {
        using var context = CreateContext();
        var auditEvent = context.Model.FindEntityType(typeof(AuditEvent))!;

        AssertNonUniqueIndex(auditEvent, nameof(AuditEvent.CreatedAt));
        AssertNonUniqueIndex(auditEvent, nameof(AuditEvent.ActorUserId));
        AssertNonUniqueIndex(auditEvent, nameof(AuditEvent.EntityType), nameof(AuditEvent.EntityId));
    }

    [Fact]
    public void Foreign_keys_use_the_approved_delete_behavior()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertForeignKey<Ingredient>(
            model, nameof(Ingredient.DepartmentId), typeof(GroceryDepartment), DeleteBehavior.Restrict);
        AssertForeignKey<Ingredient>(
            model, nameof(Ingredient.MergedIntoIngredientId), typeof(Ingredient), DeleteBehavior.Restrict);
        AssertForeignKey<IngredientAlias>(
            model, nameof(IngredientAlias.IngredientId), typeof(Ingredient), DeleteBehavior.Cascade);
        AssertForeignKey<UnitAlias>(
            model, nameof(UnitAlias.UnitId), typeof(Unit), DeleteBehavior.Cascade);
        AssertForeignKey<AuditEvent>(
            model, nameof(AuditEvent.ActorUserId), typeof(User), DeleteBehavior.Restrict);
    }

    [Fact]
    public void Store_defaults_match_the_approved_schema()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertDefaultValue(model.FindEntityType(typeof(GroceryDepartment))!, nameof(GroceryDepartment.IsActive), true);
        AssertDefaultValue(model.FindEntityType(typeof(Ingredient))!, nameof(Ingredient.IsActive), true);
        AssertDefaultValue(model.FindEntityType(typeof(Unit))!, nameof(Unit.IsCustom), false);
        AssertDefaultValue(model.FindEntityType(typeof(Unit))!, nameof(Unit.IsActive), true);
        AssertDefaultValue(model.FindEntityType(typeof(Protein))!, nameof(Protein.IsActive), true);
        AssertDefaultValue(model.FindEntityType(typeof(AuditEvent))!, nameof(AuditEvent.MetadataJson), "{}");
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
