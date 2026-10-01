using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>grocery_departments</c> table.</summary>
public sealed class GroceryDepartmentConfiguration : IEntityTypeConfiguration<GroceryDepartment>
{
    public void Configure(EntityTypeBuilder<GroceryDepartment> builder)
    {
        builder.HasKey(department => department.Id);

        builder.Property(department => department.Name).IsRequired();
        builder.Property(department => department.NormalizedName).IsRequired();
        // Store default true; the sentinel keeps an explicit false on insert
        // from being mistaken for "not set" and replaced by the default.
        builder.Property(department => department.IsActive).HasDefaultValue(true).HasSentinel(true);

        builder.HasIndex(department => department.NormalizedName).IsUnique();
    }
}
