using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>
/// Maps the singleton <c>household_settings</c> row. The check constraint keeps
/// <c>id = 1</c> as the only valid primary key.
/// </summary>
public sealed class HouseholdSettingsConfiguration : IEntityTypeConfiguration<HouseholdSettings>
{
    public void Configure(EntityTypeBuilder<HouseholdSettings> builder)
    {
        builder.ToTable(
            "household_settings",
            table => table.HasCheckConstraint("ck_household_settings_singleton", "id = 1"));

        builder.HasKey(settings => settings.Id);

        builder.Property(settings => settings.HouseholdName).IsRequired();
        builder.Property(settings => settings.Timezone).IsRequired();
        builder.Property(settings => settings.HistoricalBoundaryDate);
        builder.Property(settings => settings.StartupPage)
            .HasConversion(EnumTextConverters.StartupPageConverter)
            .IsRequired();
        builder.Property(settings => settings.AuthMode)
            .HasConversion(EnumTextConverters.AuthModeConverter)
            .IsRequired();
    }
}
