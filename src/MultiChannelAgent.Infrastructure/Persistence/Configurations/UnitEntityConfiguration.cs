using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class UnitEntityConfiguration : IEntityTypeConfiguration<UnitEntity>
{
    public void Configure(EntityTypeBuilder<UnitEntity> builder)
    {
        builder.ToTable("Units");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.CanonicalName).HasMaxLength(100).IsRequired();
        builder.Property(e => e.NormalizedCanonicalName).HasMaxLength(100).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.InventoryId);

        // Backs UnitTerm's composite (InventoryId, UnitId) FK below: a Unit can only be referenced by
        // a UnitTerm that agrees with it on InventoryId, so the term namespace can never point across
        // Inventory boundaries.
        builder.HasAlternateKey(e => new { e.InventoryId, e.Id });
    }
}
