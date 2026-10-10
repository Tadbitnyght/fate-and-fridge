using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>grocery_staples</c> table (docs/M8_PLAN.md §2.3).</summary>
public sealed class GroceryStapleConfiguration : IEntityTypeConfiguration<GroceryStaple>
{
    public void Configure(EntityTypeBuilder<GroceryStaple> builder)
    {
        builder.HasKey(staple => staple.Id);

        builder.Property(staple => staple.Name).IsRequired();
        builder.Property(staple => staple.DefaultQuantityNumeric).HasPrecision(28, 14);
        // Store default true; see GroceryDepartmentConfiguration for the sentinel.
        builder.Property(staple => staple.IsActive).HasDefaultValue(true).HasSentinel(true);

        builder.HasIndex(staple => staple.IngredientId);
        builder.HasIndex(staple => staple.DepartmentId);
        builder.HasIndex(staple => staple.UnitId);

        // Reference data is retired rather than hard-deleted, so RESTRICT
        // protects staples that point at it.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(staple => staple.IngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GroceryDepartment>()
            .WithMany()
            .HasForeignKey(staple => staple.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Unit>()
            .WithMany()
            .HasForeignKey(staple => staple.UnitId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
