using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipe_instruction_sections</c> table.</summary>
public sealed class RecipeInstructionSectionConfiguration : IEntityTypeConfiguration<RecipeInstructionSection>
{
    public void Configure(EntityTypeBuilder<RecipeInstructionSection> builder)
    {
        builder.HasKey(section => section.Id);

        builder.HasOne<Recipe>()
            .WithMany()
            .HasForeignKey(section => section.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
