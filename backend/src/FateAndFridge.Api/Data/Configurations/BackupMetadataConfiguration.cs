using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>backup_metadata</c> table (docs/DATABASE.md §12).</summary>
public sealed class BackupMetadataConfiguration : IEntityTypeConfiguration<BackupMetadata>
{
    public void Configure(EntityTypeBuilder<BackupMetadata> builder)
    {
        builder.HasKey(backup => backup.Id);

        builder.Property(backup => backup.Filename).IsRequired().HasMaxLength(255);
        builder.Property(backup => backup.AppVersion).IsRequired().HasMaxLength(50);
        builder.Property(backup => backup.SchemaVersion).IsRequired().HasMaxLength(150);
        builder.Property(backup => backup.Checksum).IsRequired().HasMaxLength(64);
        builder.Property(backup => backup.Kind)
            .HasConversion(EnumTextConverters.BackupKindConverter)
            .IsRequired();
        builder.Property(backup => backup.Status)
            .HasConversion(EnumTextConverters.BackupStatusConverter)
            .HasDefaultValue(BackupStatus.Pending)
            .IsRequired();

        // The backups list is newest-first, and file names identify one archive.
        builder.HasIndex(backup => backup.CreatedAt);
        builder.HasIndex(backup => backup.Filename).IsUnique();

        // Users are retired rather than hard-deleted; RESTRICT keeps the
        // recorded actor of a backup intact.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(backup => backup.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
