using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class InboxContentPartEntityConfiguration : IEntityTypeConfiguration<InboxContentPartEntity>
{
    public void Configure(EntityTypeBuilder<InboxContentPartEntity> builder)
    {
        builder.ToTable("InboxContentParts");

        // The order within its Turn is part of the identity: a Turn can never hold two parts claiming
        // the same position, so reassembly is always unambiguous.
        builder.HasKey(e => new { e.TurnId, e.Order });

        builder.Property(e => e.Provenance).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.Text).HasMaxLength(TurnContentPart.MaxTextLength).IsRequired();

        builder.HasOne<InboxEntryEntity>()
            .WithMany()
            .HasForeignKey(e => e.TurnId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
