using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ImportUploadEntityConfiguration : IEntityTypeConfiguration<ImportUploadEntity>
{
    public void Configure(EntityTypeBuilder<ImportUploadEntity> builder)
    {
        builder.ToTable("ImportUploads");
        builder.HasKey(entity => entity.ProposalId);
        builder.Property(entity => entity.Content).IsRequired();

        builder.HasOne<ImportProposalEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ProposalId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
