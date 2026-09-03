using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class FoundryConversationBindingEntityConfiguration : IEntityTypeConfiguration<FoundryConversationBindingEntity>
{
    public void Configure(EntityTypeBuilder<FoundryConversationBindingEntity> builder)
    {
        builder.ToTable("FoundryConversationBindings");
        builder.HasKey(e => new { e.ParticipantId, e.ChannelConversationId });

        builder.Property(e => e.ChannelConversationId).HasMaxLength(256).IsRequired();

        builder.HasOne<ParticipantEntity>()
            .WithMany()
            .HasForeignKey(e => e.ParticipantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
