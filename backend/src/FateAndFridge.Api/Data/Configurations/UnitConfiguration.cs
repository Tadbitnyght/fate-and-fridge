using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>units</c> table.</summary>
public sealed class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.HasKey(unit => unit.Id);

        builder.Property(unit => unit.Symbol).IsRequired();
        builder.Property(unit => unit.DisplayName).IsRequired();
        builder.Property(unit => unit.NormalizedName).IsRequired();
        builder.Property(unit => unit.Dimension)
            .HasConversion(EnumTextConverters.UnitDimensionConverter)
            .IsRequired();
        builder.Property(unit => unit.BaseFactor).HasPrecision(28, 14);
        builder.Property(unit => unit.IsCustom).HasDefaultValue(false);
        // Store default true; see GroceryDepartmentConfiguration for the sentinel.
        builder.Property(unit => unit.IsActive).HasDefaultValue(true).HasSentinel(true);

        builder.HasIndex(unit => unit.Symbol).IsUnique();
        builder.HasIndex(unit => unit.NormalizedName).IsUnique();
    }
}
