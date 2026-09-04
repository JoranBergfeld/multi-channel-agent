using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ConfirmationProposalEntityConfiguration : IEntityTypeConfiguration<ConfirmationProposalEntity>
{
    /// <summary>The exact text of the one status the filtered index cares about. Written literally so the filter is valid SQL on every provider.</summary>
    private const string PendingStatus = nameof(ProposalStatus.Pending);

    public void Configure(EntityTypeBuilder<ConfirmationProposalEntity> builder)
    {
        builder.ToTable("ConfirmationProposals");
        builder.HasKey(e => e.ProposalId);

        builder.Property(e => e.TokenHash).HasMaxLength(ConfirmationToken.HashTextLength).IsRequired();
        builder.Property(e => e.ChannelConversationId).HasMaxLength(InboundTurn.MaxChannelConversationIdLength).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();

        // Unbounded on purpose: the contents are bounded by ConfirmationProposal.MaxChanges, not by a
        // character count, and nvarchar(n) cannot express the ceiling that bound implies.
        builder.Property(e => e.ChangesJson).IsRequired();
        builder.Property(e => e.ExpectedVersionsJson).IsRequired();
        builder.Property(e => e.ExpectedAbsencesJson).IsRequired();

        // THE invariant of this ticket: at most one Pending proposal per Participant and
        // ChannelConversation. Enforced here rather than in code, so no race, no replica, and no
        // future caller can produce a conversation with two things "confirm" could mean. The filter
        // is written as plain SQL text valid on both SQL Server and SQLite.
        builder.HasIndex(e => new { e.ParticipantId, e.ChannelConversationId })
            .IsUnique()
            .HasFilter($"Status = '{PendingStatus}'");

        // A token can never back two proposals, whatever else goes wrong.
        builder.HasIndex(e => e.TokenHash).IsUnique();

        // Supports the expiry sweep (pending rows past their lifetime) and the retention sweep.
        builder.HasIndex(e => new { e.Status, e.ExpiresAt });
        builder.HasIndex(e => e.SettledAt);

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
