using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>recipe_instruction_steps</c> table.</summary>
public sealed class RecipeInstructionStepConfiguration : IEntityTypeConfiguration<RecipeInstructionStep>
{
    public void Configure(EntityTypeBuilder<RecipeInstructionStep> builder)
    {
        builder.HasKey(step => step.Id);

        builder.Property(step => step.Text).IsRequired();

        builder.HasIndex(step => step.SectionId);

        builder.HasOne<RecipeInstructionSection>()
            .WithMany()
            .HasForeignKey(step => step.SectionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
