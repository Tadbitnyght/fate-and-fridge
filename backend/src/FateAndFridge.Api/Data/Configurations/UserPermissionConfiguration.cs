using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>user_permissions</c> table.</summary>
public sealed class UserPermissionConfiguration : IEntityTypeConfiguration<UserPermission>
{
    public void Configure(EntityTypeBuilder<UserPermission> builder)
    {
        builder.HasKey(permission => permission.Id);

        builder.Property(permission => permission.PermissionKey).IsRequired();

        builder.HasIndex(permission => new { permission.UserId, permission.PermissionKey }).IsUnique();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(permission => permission.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
