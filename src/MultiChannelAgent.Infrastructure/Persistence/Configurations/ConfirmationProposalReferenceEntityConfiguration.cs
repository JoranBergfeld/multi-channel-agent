using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ConfirmationProposalReferenceEntityConfiguration
    : IEntityTypeConfiguration<ConfirmationProposalReferenceEntity>
{
    public void Configure(EntityTypeBuilder<ConfirmationProposalReferenceEntity> builder)
    {
        builder.ToTable("ConfirmationProposalReferences");

        // The triple is the identity: a proposal names each reference it depends on exactly once.
        builder.HasKey(e => new { e.ProposalId, e.ReferenceKind, e.ReferenceId });

        builder.Property(e => e.ReferenceKind).HasMaxLength(16).IsRequired();

        // Deliberately the *only* foreign key here. A direct Inventory FK would add a second cascade
        // path (Inventory -> ConfirmationProposals -> here, and Inventory -> here), which SQL Server
        // rejects outright - the same failure the shipped UnitTerm model records.
        builder.HasOne<ConfirmationProposalEntity>()
            .WithMany()
            .HasForeignKey(e => e.ProposalId)
            .OnDelete(DeleteBehavior.Cascade);

        // The lookup retiring a reference performs: "which pending proposals depend on this one".
        builder.HasIndex(e => new { e.ReferenceKind, e.ReferenceId });
    }
}
