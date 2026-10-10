using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>grocery_check_state</c> table (docs/M8_PLAN.md §2.3).</summary>
public sealed class GroceryCheckStateConfiguration : IEntityTypeConfiguration<GroceryCheckState>
{
    public void Configure(EntityTypeBuilder<GroceryCheckState> builder)
    {
        builder.HasKey(state => state.Id);
        // docs/DATABASE.md §9, docs/M8_PLAN.md §2.3, and PLAN.md name this table
        // in the singular, unlike the other grocery tables.
        builder.ToTable("grocery_check_state");

        builder.Property(state => state.ItemIdentityHash).IsRequired();
        builder.Property(state => state.Checked).HasDefaultValue(false);

        // One household-shared row per stable item identity.
        builder.HasIndex(state => state.ItemIdentityHash).IsUnique();
        builder.HasIndex(state => state.CheckedByUserId);

        // Users are retired rather than hard-deleted; RESTRICT preserves who
        // last touched the line.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(state => state.CheckedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
