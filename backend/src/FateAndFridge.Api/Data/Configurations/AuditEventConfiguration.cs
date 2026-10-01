using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>audit_events</c> table (docs/DATABASE.md §10).</summary>
public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(auditEvent => auditEvent.Id);

        builder.Property(auditEvent => auditEvent.EventType).IsRequired();
        builder.Property(auditEvent => auditEvent.EntityType).IsRequired();
        builder.Property(auditEvent => auditEvent.BeforeJson).HasColumnType("jsonb");
        builder.Property(auditEvent => auditEvent.AfterJson).HasColumnType("jsonb");
        builder.Property(auditEvent => auditEvent.MetadataJson)
            .HasColumnType("jsonb")
            .HasDefaultValue("{}");

        builder.HasIndex(auditEvent => auditEvent.CreatedAt);
        builder.HasIndex(auditEvent => auditEvent.ActorUserId);
        builder.HasIndex(auditEvent => new { auditEvent.EntityType, auditEvent.EntityId });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(auditEvent => auditEvent.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
