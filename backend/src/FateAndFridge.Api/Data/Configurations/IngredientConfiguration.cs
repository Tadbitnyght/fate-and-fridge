using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>ingredients</c> table.</summary>
public sealed class IngredientConfiguration : IEntityTypeConfiguration<Ingredient>
{
    public void Configure(EntityTypeBuilder<Ingredient> builder)
    {
        builder.HasKey(ingredient => ingredient.Id);

        builder.Property(ingredient => ingredient.CanonicalName).IsRequired();
        builder.Property(ingredient => ingredient.NormalizedName).IsRequired();
        // Store default true; see GroceryDepartmentConfiguration for the sentinel.
        builder.Property(ingredient => ingredient.IsActive).HasDefaultValue(true).HasSentinel(true);

        builder.HasIndex(ingredient => ingredient.NormalizedName).IsUnique();

        builder.HasOne<GroceryDepartment>()
            .WithMany()
            .HasForeignKey(ingredient => ingredient.DepartmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(ingredient => ingredient.MergedIntoIngredientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
