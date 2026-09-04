using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class LocationEntityConfiguration : IEntityTypeConfiguration<LocationEntity>
{
    public void Configure(EntityTypeBuilder<LocationEntity> builder)
    {
        builder.ToTable("Locations");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(Location.MaxNameLength).IsRequired();
        builder.Property(e => e.NormalizedName).HasMaxLength(Location.MaxNameLength).IsRequired();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Enforces "unique case-insensitively within an Inventory" against the already-normalized
        // name, over active Locations only - retiring one returns its name to the Inventory while its
        // identity remains for prior Stock Entry references and audits.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedName })
            .IsUnique()
            .HasFilter("RetiredAt IS NULL");

        builder.HasIndex(e => new { e.InventoryId, e.RetiredAt });
    }
}
