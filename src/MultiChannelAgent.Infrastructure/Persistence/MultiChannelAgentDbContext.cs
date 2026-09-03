using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Persistence;

/// <summary>
/// The single authoritative SQL Server database context for the Turn workflow tracer: durable inbox
/// acceptance, terminal Outcomes, the Delivery outbox, and coordination leases.
/// </summary>
public sealed class MultiChannelAgentDbContext(DbContextOptions<MultiChannelAgentDbContext> options)
    : DbContext(options)
{
    public DbSet<InboxEntryEntity> InboxEntries => Set<InboxEntryEntity>();

    public DbSet<OutcomeEntity> Outcomes => Set<OutcomeEntity>();

    public DbSet<DeliveryEntity> Deliveries => Set<DeliveryEntity>();

    public DbSet<LeaseEntity> Leases => Set<LeaseEntity>();

    public DbSet<ParticipantEntity> Participants => Set<ParticipantEntity>();

    public DbSet<InventoryEntity> Inventories => Set<InventoryEntity>();

    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();

    public DbSet<UnitEntity> Units => Set<UnitEntity>();

    public DbSet<UnitTermEntity> UnitTerms => Set<UnitTermEntity>();

    public DbSet<ActiveInventorySelectionEntity> ActiveInventorySelections => Set<ActiveInventorySelectionEntity>();

    public DbSet<AuthTicketEntity> AuthTickets => Set<AuthTicketEntity>();

    public DbSet<InventoryAuditEntity> InventoryAudits => Set<InventoryAuditEntity>();

    public DbSet<LocationEntity> Locations => Set<LocationEntity>();

    public DbSet<StockEntryEntity> StockEntries => Set<StockEntryEntity>();

    public DbSet<FoundryConversationBindingEntity> FoundryConversationBindings => Set<FoundryConversationBindingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MultiChannelAgentDbContext).Assembly);
    }
}
