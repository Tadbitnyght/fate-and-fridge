using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>manual_grocery_items</c> table (docs/M8_PLAN.md §2.3).</summary>
public sealed class ManualGroceryItemConfiguration : IEntityTypeConfiguration<ManualGroceryItem>
{
    public void Configure(EntityTypeBuilder<ManualGroceryItem> builder)
    {
        builder.HasKey(item => item.Id);

        builder.Property(item => item.Name).IsRequired();
        builder.Property(item => item.QuantityNumeric).HasPrecision(28, 14);
        builder.Property(item => item.SaveForFuture).HasDefaultValue(false);

        // Range scoping is the defining access pattern, so the pair is indexed.
        builder.HasIndex(item => new { item.RangeStart, item.RangeDays });
        builder.HasIndex(item => item.IngredientId);
        builder.HasIndex(item => item.DepartmentId);
        builder.HasIndex(item => item.UnitId);
        builder.HasIndex(item => item.CreatedByUserId);

        // Reference data is retired rather than hard-deleted, so RESTRICT
        // protects manual items that point at it.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(item => item.IngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<GroceryDepartment>()
            .WithMany()
            .HasForeignKey(item => item.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Unit>()
            .WithMany()
            .HasForeignKey(item => item.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        // Users are retired rather than hard-deleted; RESTRICT preserves the
        // manual item's creator attribution.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(item => item.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
