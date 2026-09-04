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

        // The ninety-day retention sweep finds rows by the age of the fact itself, so the index that
        // serves it is on the mirrored ticks the sweep actually compares - not on ExpiresAtUtc, which
        // was indexed for this job before the job existed and which no provider can be relied on to
        // compare as a DateTimeOffset.
        builder.HasIndex(e => e.OccurredAtUtcTicks);
    }
}
