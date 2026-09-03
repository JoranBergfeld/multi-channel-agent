using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class LeaseEntityConfiguration : IEntityTypeConfiguration<LeaseEntity>
{
    public void Configure(EntityTypeBuilder<LeaseEntity> builder)
    {
        builder.ToTable("Leases");
        builder.HasKey(e => e.LeaseName);

        builder.Property(e => e.LeaseName).HasMaxLength(128);
        builder.Property(e => e.OwnerId).HasMaxLength(128).IsRequired();
    }
}
