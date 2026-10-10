using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>user_grocery_preferences</c> table (docs/M8_PLAN.md §2.3).</summary>
public sealed class UserGroceryPreferenceConfiguration : IEntityTypeConfiguration<UserGroceryPreference>
{
    public void Configure(EntityTypeBuilder<UserGroceryPreference> builder)
    {
        // The owner is the primary key, so there is exactly one row per user.
        builder.HasKey(preference => preference.UserId);

        builder.Property(preference => preference.HideChecked).HasDefaultValue(false);

        // Preferences are user-owned: removing a user removes their row.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(preference => preference.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
