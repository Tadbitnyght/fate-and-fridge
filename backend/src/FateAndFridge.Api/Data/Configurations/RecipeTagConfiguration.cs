using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipe_tags</c> join table.</summary>
public sealed class RecipeTagConfiguration : IEntityTypeConfiguration<RecipeTag>
{
    public void Configure(EntityTypeBuilder<RecipeTag> builder)
    {
        builder.HasKey(link => new { link.RecipeId, link.TagId });

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(link => link.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tags are reference data; RESTRICT protects existing recipe associations.
        builder.HasOne<Tag>()
            .WithMany()
            .HasForeignKey(link => link.TagId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
