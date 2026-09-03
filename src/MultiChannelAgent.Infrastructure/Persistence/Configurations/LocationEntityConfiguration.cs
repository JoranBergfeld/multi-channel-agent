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

        // Enforces "unique case-insensitively within an Inventory" against the already-normalized name.
        builder.HasIndex(e => new { e.InventoryId, e.NormalizedName }).IsUnique();
    }
}
