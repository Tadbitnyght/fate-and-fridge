using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>ingredient_aliases</c> table.</summary>
public sealed class IngredientAliasConfiguration : IEntityTypeConfiguration<IngredientAlias>
{
    public void Configure(EntityTypeBuilder<IngredientAlias> builder)
    {
        builder.HasKey(alias => alias.Id);

        builder.Property(alias => alias.Alias).IsRequired();
        builder.Property(alias => alias.NormalizedAlias).IsRequired();

        builder.HasIndex(alias => alias.NormalizedAlias).IsUnique();

        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(alias => alias.IngredientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
