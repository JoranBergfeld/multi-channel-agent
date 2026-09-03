using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class InboxEntryEntityConfiguration : IEntityTypeConfiguration<InboxEntryEntity>
{
    public void Configure(EntityTypeBuilder<InboxEntryEntity> builder)
    {
        builder.ToTable("InboxEntries");
        builder.HasKey(e => e.TurnId);

        builder.Property(e => e.NativeMessageId).HasMaxLength(256).IsRequired();
        builder.Property(e => e.ChannelConversationId).HasMaxLength(256).IsRequired();
        builder.Property(e => e.ContentText).HasMaxLength(32 * 1024).IsRequired();
        builder.Property(e => e.Locale).HasMaxLength(32);
        builder.Property(e => e.TraceId).HasMaxLength(128);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);

        // Enforces idempotency at the Turn boundary: at-least-once redelivery of the same native
        // message can never create a second durable Turn.
        builder.HasIndex(e => e.NativeMessageId).IsUnique();

        // Supports claiming pending work in FIFO (received) order.
        builder.HasIndex(e => new { e.Status, e.ReceivedAt });
    }
}
