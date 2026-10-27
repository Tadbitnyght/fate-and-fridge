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
        builder.Property(settings => settings.RandomizerRepeatLookbackDays).HasDefaultValue(7);
        builder.Property(settings => settings.RandomizerRepeatLookaheadDays).HasDefaultValue(14);
        builder.Property(settings => settings.RandomizerRepeatMultiplier).HasPrecision(4, 2).HasDefaultValue(0.10m);
        builder.Property(settings => settings.RandomizerSpecialDayMultiplier).HasPrecision(4, 2).HasDefaultValue(0.02m);
        builder.Property(settings => settings.RandomizerProteinMultiplier).HasPrecision(4, 2).HasDefaultValue(0.70m);
        builder.Property(settings => settings.RandomizerProteinNeighborDistance).HasDefaultValue(1);
        builder.Property(settings => settings.RandomizerFavoriteMultiplier).HasPrecision(4, 2).HasDefaultValue(1.10m);
        builder.Property(settings => settings.RandomizerSpacingNearDays).HasDefaultValue(3);
        builder.Property(settings => settings.RandomizerSpacingFarDays).HasDefaultValue(8);
        builder.Property(settings => settings.RandomizerSpacingSearchDays).HasDefaultValue(90);

        builder.Property(settings => settings.StartupPage)
            .HasConversion(EnumTextConverters.StartupPageConverter)
            .IsRequired();
        builder.Property(settings => settings.AuthMode)
            .HasConversion(EnumTextConverters.AuthModeConverter)
            .IsRequired();
    }
}
