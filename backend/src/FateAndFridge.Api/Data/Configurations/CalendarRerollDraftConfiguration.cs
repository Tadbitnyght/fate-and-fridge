using FateAndFridge.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FateAndFridge.Api.Data.Configurations;

/// <summary>Maps the <c>calendar_reroll_drafts</c> table.</summary>
public sealed class CalendarRerollDraftConfiguration : IEntityTypeConfiguration<CalendarRerollDraft>
{
    public void Configure(EntityTypeBuilder<CalendarRerollDraft> builder)
    {
        builder.HasKey(draft => draft.Id);

        builder.Property(draft => draft.Mode).IsRequired();
        builder.Property(draft => draft.ProposalJson).HasColumnType("jsonb").IsRequired();

        // The opaque token is the client-facing handle; ExpiresAt drives TTL cleanup.
        builder.HasIndex(draft => draft.Token).IsUnique();
        builder.HasIndex(draft => draft.ExpiresAt);

        // Drafts are transient preview state; deleting a user discards their drafts.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(draft => draft.ActorUserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
