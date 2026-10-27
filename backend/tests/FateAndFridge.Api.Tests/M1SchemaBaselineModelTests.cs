using FateAndFridge.Api.Data;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Model-level checks for the M1 schema baseline that need no database:
/// snake_case mapping, enum text values, column types, unique indexes, and
/// foreign-key delete behavior.
/// </summary>
public sealed class M1SchemaBaselineModelTests
{
    [Fact]
    public void All_baseline_entities_are_mapped()
    {
        using var context = CreateContext();
        var model = context.Model;

        // Later milestones add entities; this check verifies the M1 baseline
        // entities remain mapped (the M2 model tests cover the full model).
        model.GetEntityTypes()
            .Select(entityType => entityType.ClrType)
            .Should().Contain(new[]
            {
                typeof(HouseholdSettings),
                typeof(ApplicationSetting),
                typeof(User),
                typeof(UserPermission),
                typeof(AuthEvent),
                typeof(UserSession),
            });
    }

    [Fact]
    public void Tables_and_columns_use_snake_case_names()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertTableName<HouseholdSettings>(model, "household_settings");
        AssertTableName<ApplicationSetting>(model, "application_settings");
        AssertTableName<User>(model, "users");
        AssertTableName<UserPermission>(model, "user_permissions");
        AssertTableName<AuthEvent>(model, "auth_events");
        AssertTableName<UserSession>(model, "user_sessions");

        var user = model.FindEntityType(typeof(User))!;
        AssertColumnName(user, nameof(User.NormalizedUsername), "normalized_username");
        AssertColumnName(user, nameof(User.MustChangePassword), "must_change_password");
        AssertColumnName(user, nameof(User.LockoutWindowStartedAt), "lockout_window_started_at");

        var applicationSetting = model.FindEntityType(typeof(ApplicationSetting))!;
        AssertColumnName(applicationSetting, nameof(ApplicationSetting.ValueJson), "value_json");

        var userSession = model.FindEntityType(typeof(UserSession))!;
        AssertColumnName(userSession, nameof(UserSession.TokenHash), "token_hash");
        AssertColumnName(userSession, nameof(UserSession.AbsoluteExpiresAt), "absolute_expires_at");
    }

    [Fact]
    public void Instant_and_json_columns_use_the_approved_store_types()
    {
        using var context = CreateContext();
        var model = context.Model;

        var user = model.FindEntityType(typeof(User))!;
        AssertColumnType(user, nameof(User.LockedUntil), "timestamp with time zone");
        AssertColumnType(user, nameof(User.LastLoginAt), "timestamp with time zone");

        var userSession = model.FindEntityType(typeof(UserSession))!;
        AssertColumnType(userSession, nameof(UserSession.AbsoluteExpiresAt), "timestamp with time zone");
        AssertColumnType(userSession, nameof(UserSession.TokenHash), "bytea");

        var householdSettings = model.FindEntityType(typeof(HouseholdSettings))!;
        AssertColumnType(householdSettings, nameof(HouseholdSettings.WeekStartsOn), "integer");

        var applicationSetting = model.FindEntityType(typeof(ApplicationSetting))!;
        AssertColumnType(applicationSetting, nameof(ApplicationSetting.ValueJson), "jsonb");

        var authEvent = model.FindEntityType(typeof(AuthEvent))!;
        AssertColumnType(authEvent, nameof(AuthEvent.MetadataJson), "jsonb");
        AssertColumnType(authEvent, nameof(AuthEvent.OccurredAt), "timestamp with time zone");
    }

    [Fact]
    public void Enum_like_columns_convert_to_snake_case_text()
    {
        using var context = CreateContext();
        var model = context.Model;

        var userRole = model.FindEntityType(typeof(User))!.GetProperty(nameof(User.Role));
        userRole.GetColumnType().Should().Be("text");
        var userRoleConverter = userRole.GetValueConverter();
        userRoleConverter.Should().NotBeNull();
        userRoleConverter!.ConvertToProvider(UserRole.Admin).Should().Be("admin");
        userRoleConverter.ConvertToProvider(UserRole.Member).Should().Be("member");
        userRoleConverter.ConvertFromProvider("admin").Should().Be(UserRole.Admin);
        userRoleConverter.ConvertFromProvider("member").Should().Be(UserRole.Member);

        var householdSettings = model.FindEntityType(typeof(HouseholdSettings))!;
        var authModeConverter = householdSettings.GetProperty(nameof(HouseholdSettings.AuthMode)).GetValueConverter();
        authModeConverter.Should().NotBeNull();
        authModeConverter!.ConvertToProvider(AuthMode.SingleUser).Should().Be("single_user");
        authModeConverter.ConvertToProvider(AuthMode.Multiuser).Should().Be("multiuser");
        authModeConverter.ConvertFromProvider("single_user").Should().Be(AuthMode.SingleUser);

        var startupPageConverter = householdSettings.GetProperty(nameof(HouseholdSettings.StartupPage)).GetValueConverter();
        startupPageConverter.Should().NotBeNull();
        startupPageConverter!.ConvertToProvider(StartupPage.Calendar).Should().Be("calendar");
        startupPageConverter.ConvertToProvider(StartupPage.Dashboard).Should().Be("dashboard");
        startupPageConverter.ConvertFromProvider("dashboard").Should().Be(StartupPage.Dashboard);
    }

    [Fact]
    public void Unknown_enum_text_fails_loudly()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertUnknownTextFails(model.FindEntityType(typeof(User))!.GetProperty(nameof(User.Role)));
        AssertUnknownTextFails(model.FindEntityType(typeof(HouseholdSettings))!.GetProperty(nameof(HouseholdSettings.AuthMode)));
        AssertUnknownTextFails(model.FindEntityType(typeof(HouseholdSettings))!.GetProperty(nameof(HouseholdSettings.StartupPage)));
    }

    [Fact]
    public void Required_unique_indexes_are_configured()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertUniqueIndex(model.FindEntityType(typeof(User))!, nameof(User.NormalizedUsername));
        AssertUniqueIndex(model.FindEntityType(typeof(UserSession))!, nameof(UserSession.TokenHash));
        AssertUniqueIndex(
            model.FindEntityType(typeof(UserPermission))!,
            nameof(UserPermission.UserId),
            nameof(UserPermission.PermissionKey));

        AssertNonUniqueIndex(model.FindEntityType(typeof(UserSession))!, nameof(UserSession.UserId));
        AssertNonUniqueIndex(model.FindEntityType(typeof(UserSession))!, nameof(UserSession.AbsoluteExpiresAt));
        AssertNonUniqueIndex(model.FindEntityType(typeof(AuthEvent))!, nameof(AuthEvent.OccurredAt));
        AssertNonUniqueIndex(model.FindEntityType(typeof(AuthEvent))!, nameof(AuthEvent.UserId));
    }

    [Fact]
    public void Foreign_keys_use_the_approved_delete_behavior()
    {
        using var context = CreateContext();
        var model = context.Model;

        AssertDeleteBehavior<UserPermission>(model, DeleteBehavior.Restrict);
        AssertDeleteBehavior<UserSession>(model, DeleteBehavior.Restrict);
        AssertDeleteBehavior<AuthEvent>(model, DeleteBehavior.SetNull);
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

    private static void AssertUnknownTextFails(IProperty property)
    {
        var converter = property.GetValueConverter();
        converter.Should().NotBeNull($"{property.Name} should have an enum text converter");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => converter!.ConvertFromProvider("bogus"));
        exception.Message.Should().Contain("Unknown");
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

    private static void AssertDeleteBehavior<TEntity>(IModel model, DeleteBehavior expected)
        where TEntity : class =>
        model.FindEntityType(typeof(TEntity))!
            .GetForeignKeys()
            .Should().ContainSingle()
            .Which.DeleteBehavior.Should().Be(expected);
}
