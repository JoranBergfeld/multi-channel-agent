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

        // Composite FK to Unit's (InventoryId, Id) alternate key rather than separate Inventory and
        // Unit FKs: this is the only cascade path into UnitTerms (Inventory -> Units -> UnitTerms),
        // avoiding the multiple-cascade-paths error a redundant direct Inventory FK would cause, and
        // it enforces that a UnitTerm's InventoryId always agrees with its Unit's InventoryId.
        builder.HasOne<UnitEntity>()
            .WithMany()
            .HasForeignKey(e => new { e.InventoryId, e.UnitId })
            .HasPrincipalKey(e => new { e.InventoryId, e.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Unit canonical names and aliases share one collision-free namespace within an Inventory:
        // a term identifies at most one active Unit.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedTerm }).IsUnique();
    }
}
