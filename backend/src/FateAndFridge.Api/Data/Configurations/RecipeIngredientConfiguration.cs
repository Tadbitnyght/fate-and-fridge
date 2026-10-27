using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipe_ingredients</c> table.</summary>
public sealed class RecipeIngredientConfiguration : IEntityTypeConfiguration<RecipeIngredient>
{
    public void Configure(EntityTypeBuilder<RecipeIngredient> builder)
    {
        builder.HasKey(recipeIngredient => recipeIngredient.Id);

        builder.Property(recipeIngredient => recipeIngredient.QuantityNumeric).HasPrecision(28, 14);
        builder.Property(recipeIngredient => recipeIngredient.IsOptional).HasDefaultValue(false);

        builder.HasIndex(recipeIngredient => recipeIngredient.RecipeId);
        builder.HasIndex(recipeIngredient => recipeIngredient.IngredientId);
        builder.HasIndex(recipeIngredient => recipeIngredient.UnitId);

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(recipeIngredient => recipeIngredient.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<RecipeIngredientGroup>()
            .WithMany()
            .HasForeignKey(recipeIngredient => recipeIngredient.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // Reference data is retired rather than hard-deleted, so RESTRICT
        // protects the ingredient/unit lines of existing recipes.
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(recipeIngredient => recipeIngredient.IngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Unit>()
            .WithMany()
            .HasForeignKey(recipeIngredient => recipeIngredient.UnitId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
