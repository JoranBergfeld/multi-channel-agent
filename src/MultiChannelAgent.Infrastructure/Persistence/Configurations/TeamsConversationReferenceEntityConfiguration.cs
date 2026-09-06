using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class TeamsConversationReferenceEntityConfiguration : IEntityTypeConfiguration<TeamsConversationReferenceEntity>
{
    public void Configure(EntityTypeBuilder<TeamsConversationReferenceEntity> builder)
    {
        builder.ToTable("TeamsConversationReferences");
        builder.HasKey(e => e.ChannelConversationId);

        builder.Property(e => e.ChannelConversationId)
            .HasMaxLength(InboundTurn.MaxChannelConversationIdLength)
            .IsRequired();

        builder.Property(e => e.SerializedReference)
            .IsRequired();
    }
}
