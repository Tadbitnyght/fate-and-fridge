using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipes</c> table.</summary>
public sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.HasKey(recipe => recipe.Id);

        builder.Property(recipe => recipe.Name).IsRequired();
        builder.Property(recipe => recipe.NormalizedName).IsRequired();
        builder.Property(recipe => recipe.Status)
            .HasConversion(EnumTextConverters.RecipeStatusConverter)
            .HasDefaultValue(RecipeStatus.Normal)
            .IsRequired();
        builder.Property(recipe => recipe.IsFavorite).HasDefaultValue(false);
        builder.Property(recipe => recipe.IsVegan).HasDefaultValue(false);
        builder.Property(recipe => recipe.IsVegetarian).HasDefaultValue(false);

        builder.HasIndex(recipe => recipe.NormalizedName).IsUnique();
        builder.HasIndex(recipe => recipe.NormalizedSourceUrl);
    }
}
