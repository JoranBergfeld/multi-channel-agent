using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ReferenceOperationEntityConfiguration : IEntityTypeConfiguration<ReferenceOperationEntity>
{
    public void Configure(EntityTypeBuilder<ReferenceOperationEntity> builder)
    {
        builder.ToTable("ReferenceOperations");

        // The operation identity IS the key, so recording one operation twice is impossible by
        // construction rather than by convention.
        builder.HasKey(e => e.OperationId);

        // The replay key. It is unique because a Turn dispatches exactly one tool call today; when
        // multi-tool-call agent runs arrive, this index must gain that call's sequence, and
        // FindRecordedByTurnAsync must take it as an argument.
        builder.HasIndex(e => new { e.InventoryId, e.ConfirmedByTurnId }).IsUnique();

        // A proposal is consumed at most once, so at most one operation can name it.
        builder.HasIndex(e => e.ProposalId).IsUnique().HasFilter("ProposalId IS NOT NULL");

        builder.HasIndex(e => e.AppliedAt);

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
