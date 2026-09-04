using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class TurnProgressEventEntityConfiguration : IEntityTypeConfiguration<TurnProgressEventEntity>
{
    public void Configure(EntityTypeBuilder<TurnProgressEventEntity> builder)
    {
        builder.ToTable("TurnProgressEvents");
        builder.HasKey(e => new { e.TurnId, e.Sequence });

        builder.Property(e => e.Kind).HasMaxLength(32).IsRequired();
        builder.HasIndex(e => e.ExpiresAtTicks);

        builder.HasOne<InboxEntryEntity>()
            .WithMany()
            .HasForeignKey(e => e.TurnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
