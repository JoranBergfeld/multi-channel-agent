using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence.Configurations;

public sealed class InventoryVersionEntityConfiguration : IEntityTypeConfiguration<InventoryVersionEntity>
{
    public void Configure(EntityTypeBuilder<InventoryVersionEntity> builder)
    {
        builder.ToTable("InventoryVersions");

        // One row per Inventory, keyed by it. That is what makes the bump a single atomic
        // "Version = Version + 1 WHERE InventoryId = @id" with no read, and therefore free of the
        // read-then-write race a counter read would have.
        builder.HasKey(e => e.InventoryId);

        builder.Property(e => e.InventoryId).ValueGeneratedNever();

        // Deliberately NO foreign key to Inventories, for exactly the reason
        // InventoryAuditEntityConfiguration gives for the audit rows this seam keys off: a fact about
        // an Inventory must stay independent of later changes to (or retirement of) the row it names.
        // A cascading foreign key here would also make the bump's fallback insertion - the guarded
        // path for an Inventory that somehow has no version row - able to fail a foreign key check
        // inside somebody else's mutating transaction, turning a state the audit model tolerates into
        // a hard failure of an unrelated write. Consistency is established by the two mechanisms that
        // actually establish it: the AddInventoryVersions migration backfills every existing
        // Inventory, and MultiChannelAgentDbContext seeds a row for every new one in the same save.
    }
}
