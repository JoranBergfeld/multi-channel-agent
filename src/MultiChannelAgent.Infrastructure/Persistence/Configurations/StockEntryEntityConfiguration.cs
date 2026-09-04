using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class StockEntryEntityConfiguration : IEntityTypeConfiguration<StockEntryEntity>
{
    public void Configure(EntityTypeBuilder<StockEntryEntity> builder)
    {
        builder.ToTable("StockEntries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(StockEntry.MaxNameLength).IsRequired();
        builder.Property(e => e.NormalizedName).HasMaxLength(StockEntry.MaxNameLength).IsRequired();
        builder.Property(e => e.Note).HasMaxLength(StockEntry.MaxNoteLength);

        // A generous fixed precision/scale so a Quantity's exact decimal value is never silently
        // rounded by the column - CONTEXT.md requires Quantity be exposed exactly, and different
        // Units are never auto-converted, so there is no unit-driven upper bound on the scale callers
        // might reasonably need.
        builder.Property(e => e.Quantity).HasPrecision(28, 10);
        builder.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<UnitEntity>()
            .WithMany()
            .HasForeignKey(e => e.UnitId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<LocationEntity>()
            .WithMany()
            .HasForeignKey(e => e.LocationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Enforces Equivalent Stock: the same normalized name, Unit, and Location within the same
        // Inventory is always one row, never a duplicate. It is enforced against the real Location
        // column rather than a mirrored sentinel one, so no writer can bypass the constraint by
        // mismaintaining a mirror. Two filtered indexes are needed because a relational unique index
        // treats every NULL as distinct from every other NULL, which alone would let any number of
        // unlocated rows for one name and Unit slip past: the first covers placed Stock, the second
        // covers unlocated Stock, where "no Location" is itself the whole key. The filter text is
        // written unquoted so it is valid SQL on every provider this model is created on.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedName, e.UnitId, e.LocationId })
            .IsUnique()
            .HasFilter("LocationId IS NOT NULL");

        builder.HasIndex(e => new { e.InventoryId, e.NormalizedName, e.UnitId })
            .IsUnique()
            .HasFilter("LocationId IS NULL");

        // Supports List/Find scoping and filtering by Inventory, then by name/Location.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedName });
        builder.HasIndex(e => new { e.InventoryId, e.LocationId });
    }
}
