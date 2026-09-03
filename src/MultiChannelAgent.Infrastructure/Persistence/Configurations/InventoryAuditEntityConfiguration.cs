using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class InventoryAuditEntityConfiguration : IEntityTypeConfiguration<InventoryAuditEntity>
{
    public void Configure(EntityTypeBuilder<InventoryAuditEntity> builder)
    {
        builder.ToTable("InventoryAudits");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ActorKind).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ActorId).HasMaxLength(128).IsRequired();
        builder.Property(e => e.OutcomeCode).HasMaxLength(64).IsRequired();

        // No foreign keys to Inventories/Participants: an audit row must remain a durable, minimal
        // fact independent of later changes to (or eventual retirement of) either referenced row.
        builder.HasIndex(e => e.InventoryId);

        // Supports a future 90-day-expiry sweeping job; not exercised by this ticket, but the access
        // pattern (find rows past their expiry) is common enough to index up front, matching
        // AuthTicketEntityConfiguration's ExpiresAtUtc index.
        builder.HasIndex(e => e.ExpiresAtUtc);
    }
}
