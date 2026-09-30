using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>auth_events</c> table.</summary>
public sealed class AuthEventConfiguration : IEntityTypeConfiguration<AuthEvent>
{
    public void Configure(EntityTypeBuilder<AuthEvent> builder)
    {
        builder.HasKey(authEvent => authEvent.Id);

        builder.Property(authEvent => authEvent.EventType).IsRequired();
        builder.Property(authEvent => authEvent.MetadataJson).HasColumnType("jsonb");

        builder.HasIndex(authEvent => authEvent.OccurredAt);
        builder.HasIndex(authEvent => authEvent.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(authEvent => authEvent.UserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
