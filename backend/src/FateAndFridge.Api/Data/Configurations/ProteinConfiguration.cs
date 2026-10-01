using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>proteins</c> table.</summary>
public sealed class ProteinConfiguration : IEntityTypeConfiguration<Protein>
{
    public void Configure(EntityTypeBuilder<Protein> builder)
    {
        builder.HasKey(protein => protein.Id);

        builder.Property(protein => protein.Name).IsRequired();
        builder.Property(protein => protein.NormalizedName).IsRequired();
        // Store default true; see GroceryDepartmentConfiguration for the sentinel.
        builder.Property(protein => protein.IsActive).HasDefaultValue(true).HasSentinel(true);

        builder.HasIndex(protein => protein.NormalizedName).IsUnique();
    }
}
