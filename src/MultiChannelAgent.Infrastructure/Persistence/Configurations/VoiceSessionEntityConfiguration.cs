using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class VoiceSessionEntityConfiguration : IEntityTypeConfiguration<VoiceSessionEntity>
{
    public void Configure(EntityTypeBuilder<VoiceSessionEntity> builder)
    {
        builder.ToTable("VoiceSessions");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ChannelConversationId).HasMaxLength(512).IsRequired();
        builder.Property(e => e.ControlSessionId).HasMaxLength(512);
        builder.Property(e => e.OwnerInstanceId).HasMaxLength(256).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();

        // Per-participant filtered unique index: only one slot-occupying session per participant.
        builder.HasIndex(e => e.ParticipantId)
            .IsUnique()
            .HasFilter("[OccupiesSlot] = 1")
            .HasDatabaseName("IX_VoiceSessions_ParticipantId_OccupiesSlot");

        // Global cap counts slot-occupying sessions — a filtered index on OccupiesSlot enables the
        // SERIALIZABLE COUNT to range-lock on a tight predicate rather than scanning the whole table.
        builder.HasIndex(e => e.OccupiesSlot)
            .HasDatabaseName("IX_VoiceSessions_OccupiesSlot");

        // Supports FindExpiredOrIdleAsync queries.
        builder.HasIndex(e => new { e.Status, e.ExpiresAtTicks, e.IdleExpiresAtTicks })
            .HasDatabaseName("IX_VoiceSessions_Status_Expiry");

        // Supports FindByOwnerInstanceAsync queries.
        builder.HasIndex(e => new { e.OwnerInstanceId, e.Status })
            .HasDatabaseName("IX_VoiceSessions_Owner_Status");
    }
}
