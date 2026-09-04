using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class ReferenceEffectEntityConfiguration : IEntityTypeConfiguration<ReferenceEffectEntity>
{
    public void Configure(EntityTypeBuilder<ReferenceEffectEntity> builder)
    {
        builder.ToTable("ReferenceEffects");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ReferenceKind).HasMaxLength(16).IsRequired();
        builder.Property(e => e.Name).HasMaxLength(Location.MaxNameLength).IsRequired();
        builder.Property(e => e.NewName).HasMaxLength(Location.MaxNameLength);
        builder.Property(e => e.Alias).HasMaxLength(Unit.MaxNameLength);

        // Unbounded on purpose: the contents are bounded by the number of aliases a create may carry,
        // not by a character count, and nvarchar(n) cannot express that ceiling.
        builder.Property(e => e.AliasesJson);

        // The only foreign key, so there is exactly one cascade path
        // (Inventory -> ReferenceOperations -> here).
        builder.HasOne<ReferenceOperationEntity>()
            .WithMany()
            .HasForeignKey(e => e.OperationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.OperationId, e.Order }).IsUnique();
    }
}
