using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipe_ingredient_groups</c> table.</summary>
public sealed class RecipeIngredientGroupConfiguration : IEntityTypeConfiguration<RecipeIngredientGroup>
{
    public void Configure(EntityTypeBuilder<RecipeIngredientGroup> builder)
    {
        builder.HasKey(group => group.Id);

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(group => group.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
