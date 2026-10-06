using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>calendar_entries</c> table (docs/DATABASE.md §8).</summary>
public sealed class CalendarEntryConfiguration : IEntityTypeConfiguration<CalendarEntry>
{
    public void Configure(EntityTypeBuilder<CalendarEntry> builder)
    {
        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.EntryType)
            .HasConversion(EnumTextConverters.CalendarEntryTypeConverter)
            .IsRequired();
        builder.Property(entry => entry.SnapshotDisplayText).IsRequired();
        builder.Property(entry => entry.IncludeGroceries).HasDefaultValue(false);
        builder.Property(entry => entry.IsLocked).HasDefaultValue(false);

        // One entry per household-local date.
        builder.HasIndex(entry => entry.MealDate).IsUnique();
        builder.HasIndex(entry => entry.RecipeId);

        builder.HasOne(entry => entry.Recipe)
            .WithMany()
            .HasForeignKey(entry => entry.RecipeId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(entry => entry.UpdatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
