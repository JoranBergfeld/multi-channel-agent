using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class StockOperationEntityConfiguration : IEntityTypeConfiguration<StockOperationEntity>
{
    /// <summary>Matches <c>UnitEntityConfiguration</c>'s canonical name length, since this column stores a copy of one.</summary>
    private const int UnitCanonicalNameLength = 100;

    public void Configure(EntityTypeBuilder<StockOperationEntity> builder)
    {
        builder.ToTable("StockOperations");

        // The operation identity IS the key, so recording the same operation twice is impossible by
        // construction rather than by convention: the second insert cannot land at all.
        builder.HasKey(e => e.OperationId);

        builder.Property(e => e.Kind).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(StockEntry.MaxNameLength).IsRequired();
        builder.Property(e => e.UnitCanonicalName).HasMaxLength(UnitCanonicalNameLength).IsRequired();
        builder.Property(e => e.LocationName).HasMaxLength(Location.MaxNameLength);
        builder.Property(e => e.Note).HasMaxLength(StockEntry.MaxNoteLength);

        // The same precision and scale StockEntries uses, so a recorded amount is byte-for-byte the
        // amount that was written and a retry re-reports it exactly.
        builder.Property(e => e.PreviousQuantity).HasPrecision(28, 10);
        builder.Property(e => e.ResultingQuantity).HasPrecision(28, 10);

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Supports a later retention sweep over old ledger rows. It is not exercised by this ticket,
        // matching how InventoryAuditEntityConfiguration indexes its own expiry up front.
        builder.HasIndex(e => e.AppliedAt);
    }
}
