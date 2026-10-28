using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>unit_aliases</c> table.</summary>
public sealed class UnitAliasConfiguration : IEntityTypeConfiguration<UnitAlias>
{
    public void Configure(EntityTypeBuilder<UnitAlias> builder)
    {
        builder.HasKey(alias => alias.Id);

        builder.Property(alias => alias.Alias).IsRequired();
        builder.Property(alias => alias.NormalizedAlias).IsRequired();

        // Per-unit uniqueness: seeded "t" (teaspoon) and "T" (tablespoon) both
        // normalize to "t" but belong to different units.
        builder.HasIndex(alias => new { alias.UnitId, alias.NormalizedAlias }).IsUnique();

        builder.HasOne<Unit>()
            .WithMany()
            .HasForeignKey(alias => alias.UnitId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
