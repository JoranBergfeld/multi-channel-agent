using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ImportProposalEntityConfiguration : IEntityTypeConfiguration<ImportProposalEntity>
{
    private const string PendingStatus = nameof(ImportProposalStatus.Pending);

    public void Configure(EntityTypeBuilder<ImportProposalEntity> builder)
    {
        builder.ToTable("ImportProposals");
        builder.HasKey(entity => entity.ProposalId);

        builder.Property(entity => entity.TokenHash).HasMaxLength(ConfirmationToken.HashTextLength).IsRequired();
        builder.Property(entity => entity.FileDigest).HasMaxLength(64).IsRequired();
        builder.Property(entity => entity.Status).HasMaxLength(32).IsRequired();
        builder.Property(entity => entity.EntriesJson).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ParticipantEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ParticipantId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(entity => new { entity.ParticipantId, entity.InventoryId })
            .IsUnique()
            .HasFilter($"Status = '{PendingStatus}'");
        builder.HasIndex(entity => entity.TokenHash).IsUnique();
        builder.HasIndex(entity => new { entity.Status, entity.ExpiresAtTicks });
        builder.HasIndex(entity => entity.SettledAtTicks);
    }
}
