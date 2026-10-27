using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipe_images</c> table.</summary>
public sealed class RecipeImageConfiguration : IEntityTypeConfiguration<RecipeImage>
{
    public void Configure(EntityTypeBuilder<RecipeImage> builder)
    {
        builder.HasKey(image => image.Id);

        builder.Property(image => image.StoragePath).IsRequired();

        // One active image per recipe; replacement and removal are atomic.
        builder.HasIndex(image => image.RecipeId).IsUnique();

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(image => image.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
