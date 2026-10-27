using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>calendar_entry_history</c> table (docs/DATABASE.md §8).</summary>
public sealed class CalendarEntryHistoryConfiguration : IEntityTypeConfiguration<CalendarEntryHistory>
{
    public void Configure(EntityTypeBuilder<CalendarEntryHistory> builder)
    {
        builder.HasKey(history => history.Id);

        builder.Property(history => history.BeforeJson).HasColumnType("jsonb");
        builder.Property(history => history.AfterJson).HasColumnType("jsonb");
        builder.Property(history => history.MetadataJson).HasColumnType("jsonb");
        builder.Property(history => history.IsHistoricalCorrection).HasDefaultValue(false);
        builder.Property(history => history.BatchOperationId).HasColumnType("uuid");

        builder.HasIndex(history => history.CalendarEntryId);
        builder.HasIndex(history => history.OperationId);
        builder.HasIndex(history => history.BatchOperationId);
        builder.HasIndex(history => history.ChangedAt);

        // History outlives its entry: hard deletes null the reference but keep
        // the copied meal date and before/after snapshot.
        builder.HasOne<CalendarEntry>()
            .WithMany()
            .HasForeignKey(history => history.CalendarEntryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(history => history.ChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
