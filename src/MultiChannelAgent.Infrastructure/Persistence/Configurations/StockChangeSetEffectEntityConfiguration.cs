using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class StockChangeSetEffectEntityConfiguration : IEntityTypeConfiguration<StockChangeSetEffectEntity>
{
    /// <summary>Matches <c>UnitEntityConfiguration</c>'s canonical name length, since these columns store copies of one.</summary>
    private const int UnitCanonicalNameLength = 100;

    public void Configure(EntityTypeBuilder<StockChangeSetEffectEntity> builder)
    {
        builder.ToTable("StockChangeSetEffects");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Effect).HasMaxLength(32).IsRequired();
        builder.Property(e => e.SourceName).HasMaxLength(StockEntry.MaxNameLength).IsRequired();
        builder.Property(e => e.SourceUnitCanonicalName).HasMaxLength(UnitCanonicalNameLength).IsRequired();
        builder.Property(e => e.SourceLocationName).HasMaxLength(Location.MaxNameLength);
        builder.Property(e => e.DestinationName).HasMaxLength(StockEntry.MaxNameLength);
        builder.Property(e => e.DestinationUnitCanonicalName).HasMaxLength(UnitCanonicalNameLength);
        builder.Property(e => e.DestinationLocationName).HasMaxLength(Location.MaxNameLength);

        // The same precision and scale StockEntries uses, so a recorded amount is byte-for-byte the
        // amount that was written and a retry re-reports it exactly.
        builder.Property(e => e.SourcePreviousQuantity).HasPrecision(28, 10);
        builder.Property(e => e.SourceResultingQuantity).HasPrecision(28, 10);
        builder.Property(e => e.DestinationPreviousQuantity).HasPrecision(28, 10);
        builder.Property(e => e.DestinationResultingQuantity).HasPrecision(28, 10);
        builder.Property(e => e.TransferredQuantity).HasPrecision(28, 10);
        builder.Property(e => e.NewName).HasMaxLength(StockEntry.MaxNameLength);

        builder.HasOne<StockChangeSetOperationEntity>()
            .WithMany()
            .HasForeignKey(e => e.OperationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Reads are always "every effect of this operation, in order".
        builder.HasIndex(e => new { e.OperationId, e.Order }).IsUnique();
    }
}
