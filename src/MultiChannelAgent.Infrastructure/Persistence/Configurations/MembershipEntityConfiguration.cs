using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class MembershipEntityConfiguration : IEntityTypeConfiguration<MembershipEntity>
{
    public void Configure(EntityTypeBuilder<MembershipEntity> builder)
    {
        builder.ToTable("Memberships");
        builder.HasKey(e => new { e.InventoryId, e.ParticipantId });

        builder.Property(e => e.Role).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasOne<InventoryEntity>()
            .WithMany()
            .HasForeignKey(e => e.InventoryId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ParticipantEntity>()
            .WithMany()
            .HasForeignKey(e => e.ParticipantId)
            .OnDelete(DeleteBehavior.Restrict);

        // Every Inventory starts with exactly one Owner: enforced at the database layer, not just in
        // application code, via a filtered unique index that only applies to Owner rows.
        builder.HasIndex(e => e.InventoryId)
            .IsUnique()
            .HasFilter("\"Role\" = 'Owner'")
            .HasDatabaseName("IX_Memberships_InventoryId_OneOwner");

        // Supports authorized-listing lookups by Participant.
        builder.HasIndex(e => e.ParticipantId);
    }
}
