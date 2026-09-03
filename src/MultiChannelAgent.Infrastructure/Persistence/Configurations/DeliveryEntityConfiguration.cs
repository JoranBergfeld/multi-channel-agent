using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class DeliveryEntityConfiguration : IEntityTypeConfiguration<DeliveryEntity>
{
    public void Configure(EntityTypeBuilder<DeliveryEntity> builder)
    {
        builder.ToTable("Deliveries");
        builder.HasKey(e => e.DeliveryId);

        builder.Property(e => e.Channel).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Payload).HasMaxLength(32 * 1024).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);

        builder.HasOne<InboxEntryEntity>()
            .WithMany()
            .HasForeignKey(e => e.TurnId)
            .OnDelete(DeleteBehavior.Cascade);

        // Supports claiming pending deliveries for dispatch.
        builder.HasIndex(e => new { e.Status, e.CreatedAt });
        builder.HasIndex(e => e.TurnId);
    }
}
