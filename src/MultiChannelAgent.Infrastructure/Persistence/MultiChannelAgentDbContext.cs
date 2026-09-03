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

    public DbSet<InboxContentPartEntity> InboxContentParts => Set<InboxContentPartEntity>();

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

    public DbSet<StockOperationEntity> StockOperations => Set<StockOperationEntity>();

    public DbSet<FoundryConversationBindingEntity> FoundryConversationBindings => Set<FoundryConversationBindingEntity>();

    /// <summary>
    /// The columns Stock ordering and keyset pagination compare
    /// (<see cref="Domain.Inventories.StockEntryOrderKey"/>). They must compare exactly as the domain
    /// does - ordinally - so the database's order is the domain's order rather than a locale-dependent
    /// approximation of it.
    /// </summary>
    private static readonly (Type EntityType, string PropertyName)[] OrdinalOrderKeyColumns =
    [
        (typeof(StockEntryEntity), nameof(StockEntryEntity.NormalizedName)),
        (typeof(UnitEntity), nameof(UnitEntity.NormalizedCanonicalName)),
        (typeof(LocationEntity), nameof(LocationEntity.NormalizedName)),
    ];

    /// <summary>
    /// SQL Server's default collations compare text by locale rules (case- and accent-insensitive,
    /// with locale-specific ordering), which would order and compare the normalized order keys
    /// differently from the domain. A binary collation makes the comparison ordinal, exactly like
    /// <see cref="string.CompareOrdinal"/>. SQLite already compares text binarily by default, so it
    /// needs nothing here.
    /// </summary>
    private const string OrdinalSqlServerCollation = "Latin1_General_100_BIN2";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MultiChannelAgentDbContext).Assembly);

        if (!Database.IsSqlServer())
        {
            return;
        }

        foreach (var (entityType, propertyName) in OrdinalOrderKeyColumns)
        {
            modelBuilder.Entity(entityType).Property(propertyName).UseCollation(OrdinalSqlServerCollation);
        }
    }
}
