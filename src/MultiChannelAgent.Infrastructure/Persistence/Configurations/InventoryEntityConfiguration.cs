using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class InventoryEntityConfiguration : IEntityTypeConfiguration<InventoryEntity>
{
    public void Configure(EntityTypeBuilder<InventoryEntity> builder)
    {
        builder.ToTable("Inventories");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(Inventory.MaxNameLength).IsRequired();
        builder.Property(e => e.NormalizedName).HasMaxLength(Inventory.MaxNameLength).IsRequired();
        builder.Property(e => e.ClientRequestId).HasMaxLength(Inventory.MaxClientRequestIdLength).IsRequired();

        builder.HasOne<ParticipantEntity>()
            .WithMany()
            .HasForeignKey(e => e.CreatedByParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Enforces creation idempotency: resubmitting the same (creator, ClientRequestId) pair -
        // including two concurrent deliveries of it - can never create a second Inventory.
        builder.HasIndex(e => new { e.CreatedByParticipantId, e.ClientRequestId }).IsUnique();

        // Supports deterministic authorized-listing order (normalized name, then short id).
        builder.HasIndex(e => e.NormalizedName);
    }
}
