using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ActiveInventorySelectionEntityConfiguration : IEntityTypeConfiguration<ActiveInventorySelectionEntity>
{
    public void Configure(EntityTypeBuilder<ActiveInventorySelectionEntity> builder)
    {
        builder.ToTable("ActiveInventorySelections");
        builder.HasKey(e => new { e.ParticipantId, e.ChannelConversationId });

        builder.Property(e => e.ChannelConversationId).HasMaxLength(256).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.InventoryId);
    }
}
