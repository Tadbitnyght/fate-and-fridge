using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>users</c> table.</summary>
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(user => user.Id);

        builder.Property(user => user.Username).IsRequired();
        builder.Property(user => user.NormalizedUsername).IsRequired();
        builder.Property(user => user.PasswordHash).IsRequired();
        builder.Property(user => user.Role)
            .HasConversion(EnumTextConverters.UserRoleConverter)
            .IsRequired();

        builder.HasIndex(user => user.NormalizedUsername).IsUnique();
    }
}
