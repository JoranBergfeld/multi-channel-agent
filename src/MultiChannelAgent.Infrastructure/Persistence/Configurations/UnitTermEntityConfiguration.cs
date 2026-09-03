using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class UnitTermEntityConfiguration : IEntityTypeConfiguration<UnitTermEntity>
{
    public void Configure(EntityTypeBuilder<UnitTermEntity> builder)
    {
        builder.ToTable("UnitTerms");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Term).HasMaxLength(100).IsRequired();
        builder.Property(e => e.NormalizedTerm).HasMaxLength(100).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<UnitEntity>()
            .WithMany()
            .HasForeignKey(e => e.UnitId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unit canonical names and aliases share one collision-free namespace within an Inventory:
        // a term identifies at most one active Unit.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedTerm }).IsUnique();
    }
}
