using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>user_sessions</c> table.</summary>
public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.HasKey(session => session.Id);

        builder.Property(session => session.TokenHash).IsRequired();

        builder.HasIndex(session => session.TokenHash).IsUnique();
        builder.HasIndex(session => session.UserId);
        builder.HasIndex(session => session.AbsoluteExpiresAt);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
