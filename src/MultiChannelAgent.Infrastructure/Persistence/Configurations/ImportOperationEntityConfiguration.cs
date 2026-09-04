using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ImportOperationEntityConfiguration : IEntityTypeConfiguration<ImportOperationEntity>
{
    public void Configure(EntityTypeBuilder<ImportOperationEntity> builder)
    {
        builder.ToTable("ImportOperations");
        builder.HasKey(entity => entity.OperationId);
        builder.Property(entity => entity.FileDigest).HasMaxLength(64).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ParticipantEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ActorId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(entity => entity.ProposalId).IsUnique();
    }
}
