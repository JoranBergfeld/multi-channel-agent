using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class InboxEntryEntityConfiguration : IEntityTypeConfiguration<InboxEntryEntity>
{
    public void Configure(EntityTypeBuilder<InboxEntryEntity> builder)
    {
        builder.ToTable("InboxEntries");
        builder.HasKey(e => e.TurnId);

        // The same constants the domain validates against, so a value it accepts always fits here.
        builder.Property(e => e.NativeMessageId).HasMaxLength(InboundTurn.MaxNativeMessageIdLength).IsRequired();
        builder.Property(e => e.ChannelConversationId).HasMaxLength(InboundTurn.MaxChannelConversationIdLength).IsRequired();
        builder.Property(e => e.Channel).HasMaxLength(InboundTurn.MaxChannelLength).IsRequired();
        builder.Property(e => e.PrincipalKind).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.PrincipalSubject).HasMaxLength(256).IsRequired();
        builder.Property(e => e.PrincipalTenantId).HasMaxLength(128);
        builder.Property(e => e.Locale).HasMaxLength(InboundTurn.MaxLocaleLength);
        builder.Property(e => e.TraceId).HasMaxLength(InboundTurn.MaxTraceIdLength);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.InputModality).HasConversion<string>().HasMaxLength(16).HasDefaultValueSql("'Text'");

        // Enforces idempotency at the Turn boundary, scoped the way a native message id is actually
        // unique: within the Participant and ChannelConversation that issued it. At-least-once
        // redelivery of the same native message in the same scope can never create a second durable
        // Turn, while the same opaque id issued in a different scope stays a distinct Turn.
        builder.HasIndex(e => new { e.ParticipantId, e.ChannelConversationId, e.NativeMessageId }).IsUnique();

        // The durable acceptance order within a ChannelConversation. Uniqueness is what makes
        // assigning it race-safe: two concurrent acceptances that compute the same next sequence
        // cannot both commit, so the loser recomputes instead of silently duplicating an order key
        // that per-conversation FIFO depends on being total.
        builder.HasIndex(e => new { e.ChannelConversationId, e.ConversationSequence }).IsUnique();

        // Supports claiming pending work oldest-first, by the same key the claim orders on.
        builder.HasIndex(e => new { e.Status, e.ReceivedAtTicks });

        // Supports the conversation-head claim: for a candidate Turn, resolving whether its
        // ChannelConversation still has an earlier outstanding (pending or in-flight) Turn.
        builder.HasIndex(e => new { e.ChannelConversationId, e.Status, e.ConversationSequence });
    }
}
