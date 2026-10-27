using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipe_proteins</c> join table.</summary>
public sealed class RecipeProteinConfiguration : IEntityTypeConfiguration<RecipeProtein>
{
    public void Configure(EntityTypeBuilder<RecipeProtein> builder)
    {
        builder.HasKey(link => new { link.RecipeId, link.ProteinId });

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(link => link.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Proteins are retired rather than hard-deleted; RESTRICT protects
        // existing recipe associations.
        builder.HasOne<Protein>()
            .WithMany()
            .HasForeignKey(link => link.ProteinId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
