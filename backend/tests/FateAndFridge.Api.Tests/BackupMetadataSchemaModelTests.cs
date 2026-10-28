using FateAndFridge.Api.Data;
using FateAndFridge.Api.Data.Configurations;
using FateAndFridge.Api.Domain;
using FateAndFridge.Api.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Model-level checks for the <c>backup_metadata</c> schema
/// that need no database
/// snake_case naming, store types and defaults, the unique filename, the
/// creator foreign key, and the stable text conversion of kind/status.
/// </summary>
public sealed class BackupMetadataSchemaModelTests
{
    [Fact]
    public void Backup_metadata_entity_is_mapped()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(BackupMetadata));

        entityType.Should().NotBeNull();
        entityType!.GetTableName().Should().Be("backup_metadata");
        entityType.GetSchema().Should().BeNull();
    }

    [Fact]
    public void Backup_metadata_columns_use_snake_case_names()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(BackupMetadata))!;

        AssertColumnName(entityType, nameof(BackupMetadata.Id), "id");
        AssertColumnName(entityType, nameof(BackupMetadata.Filename), "filename");
        AssertColumnName(entityType, nameof(BackupMetadata.AppVersion), "app_version");
        AssertColumnName(entityType, nameof(BackupMetadata.SchemaVersion), "schema_version");
        AssertColumnName(entityType, nameof(BackupMetadata.CreatedAt), "created_at");
        AssertColumnName(entityType, nameof(BackupMetadata.CreatedByUserId), "created_by_user_id");
        AssertColumnName(entityType, nameof(BackupMetadata.Checksum), "checksum");
        AssertColumnName(entityType, nameof(BackupMetadata.SizeBytes), "size_bytes");
        AssertColumnName(entityType, nameof(BackupMetadata.Kind), "kind");
        AssertColumnName(entityType, nameof(BackupMetadata.Status), "status");
        AssertColumnName(entityType, nameof(BackupMetadata.Error), "error");
    }

    [Fact]
    public void Backup_metadata_uses_the_approved_store_types()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(BackupMetadata))!;

        AssertColumnType(entityType, nameof(BackupMetadata.Id), "bigint");
        AssertColumnType(entityType, nameof(BackupMetadata.CreatedAt), "timestamp with time zone");
        AssertColumnType(entityType, nameof(BackupMetadata.SizeBytes), "bigint");
        AssertColumnType(entityType, nameof(BackupMetadata.Kind), "text");
        AssertColumnType(entityType, nameof(BackupMetadata.Status), "text");
        AssertColumnType(entityType, nameof(BackupMetadata.Error), "text");

        entityType.GetProperty(nameof(BackupMetadata.Filename)).GetMaxLength().Should().Be(255);
        entityType.GetProperty(nameof(BackupMetadata.AppVersion)).GetMaxLength().Should().Be(50);
        entityType.GetProperty(nameof(BackupMetadata.SchemaVersion)).GetMaxLength().Should().Be(150);
        entityType.GetProperty(nameof(BackupMetadata.Checksum)).GetMaxLength().Should().Be(64);
    }

    [Fact]
    public void Backup_metadata_required_fields_are_not_nullable()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(BackupMetadata))!;

        foreach (var propertyName in new[]
                 {
                     nameof(BackupMetadata.Filename),
                     nameof(BackupMetadata.AppVersion),
                     nameof(BackupMetadata.SchemaVersion),
                     nameof(BackupMetadata.CreatedAt),
                     nameof(BackupMetadata.Checksum),
                     nameof(BackupMetadata.SizeBytes),
                     nameof(BackupMetadata.Kind),
                     nameof(BackupMetadata.Status),
                 })
        {
            entityType.GetProperty(propertyName).IsNullable.Should().BeFalse($"{propertyName} is required");
        }

        entityType.GetProperty(nameof(BackupMetadata.CreatedByUserId)).IsNullable.Should().BeTrue();
        entityType.GetProperty(nameof(BackupMetadata.Error)).IsNullable.Should().BeTrue();
    }

    [Fact]
    public void Backup_filename_is_unique_and_created_at_is_indexed_for_the_list()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(BackupMetadata))!;

        var filenameIndex = entityType.GetIndexes()
            .Should().ContainSingle(index => index.Properties.Count == 1
                && index.Properties[0].Name == nameof(BackupMetadata.Filename))
            .Which;
        filenameIndex.IsUnique.Should().BeTrue();

        entityType.GetIndexes()
            .Should().Contain(index => !index.IsUnique
                && index.Properties.Count == 1
                && index.Properties[0].Name == nameof(BackupMetadata.CreatedAt));
    }

    [Fact]
    public void Backup_creator_foreign_key_restricts_user_deletes()
    {
        using var context = CreateContext();
        var foreignKey = context.Model.FindEntityType(typeof(BackupMetadata))!
            .GetForeignKeys()
            .Should().ContainSingle(candidate => candidate.Properties.Count == 1
                && candidate.Properties[0].Name == nameof(BackupMetadata.CreatedByUserId))
            .Which;

        foreignKey.PrincipalEntityType.ClrType.Should().Be(typeof(User));
        foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
    }

    [Fact]
    public void Backup_status_defaults_to_pending_and_kind_is_required()
    {
        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(BackupMetadata))!;

        entityType.GetProperty(nameof(BackupMetadata.Status))
            .GetDefaultValue().Should().Be(BackupStatus.Pending);
        entityType.GetProperty(nameof(BackupMetadata.Kind))
            .FindAnnotation(RelationalAnnotationNames.DefaultValue).Should().BeNull();
    }

    [Fact]
    public void Backup_kind_and_status_use_stable_snake_case_text()
    {
        EnumTextConverters.BackupKindToText(BackupKind.Manual).Should().Be("manual");
        EnumTextConverters.BackupKindToText(BackupKind.Safety).Should().Be("safety");

        EnumTextConverters.BackupStatusToText(BackupStatus.Pending).Should().Be("pending");
        EnumTextConverters.BackupStatusToText(BackupStatus.Completed).Should().Be("completed");
        EnumTextConverters.BackupStatusToText(BackupStatus.Failed).Should().Be("failed");
        EnumTextConverters.BackupStatusToText(BackupStatus.Restored).Should().Be("restored");
        EnumTextConverters.BackupStatusToText(BackupStatus.Deleted).Should().Be("deleted");

        EnumTextConverters.TryBackupKindFromText("safety", out var kind).Should().BeTrue();
        kind.Should().Be(BackupKind.Safety);
        EnumTextConverters.TryBackupKindFromText("manual", out var manualKind).Should().BeTrue();
        manualKind.Should().Be(BackupKind.Manual);

        EnumTextConverters.TryBackupStatusFromText("restored", out var status).Should().BeTrue();
        status.Should().Be(BackupStatus.Restored);
        EnumTextConverters.TryBackupStatusFromText("unknown", out _).Should().BeFalse();
        EnumTextConverters.TryBackupKindFromText("unknown", out _).Should().BeFalse();

        using var context = CreateContext();
        var entityType = context.Model.FindEntityType(typeof(BackupMetadata))!;
        entityType.GetProperty(nameof(BackupMetadata.Kind))
            .GetValueConverter()!.ProviderClrType.Should().Be(typeof(string));
        entityType.GetProperty(nameof(BackupMetadata.Status))
            .GetValueConverter()!.ProviderClrType.Should().Be(typeof(string));
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ApiFactory.DummyConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static void AssertColumnName(IEntityType entityType, string propertyName, string expectedColumnName) =>
        entityType.GetProperty(propertyName).GetColumnName().Should().Be(expectedColumnName);

    private static void AssertColumnType(IEntityType entityType, string propertyName, string expectedColumnType) =>
        entityType.GetProperty(propertyName).GetColumnType().Should().Be(expectedColumnType);
}
