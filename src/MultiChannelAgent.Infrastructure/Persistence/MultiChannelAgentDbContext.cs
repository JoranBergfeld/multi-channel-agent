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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MultiChannelAgentDbContext).Assembly);
    }
}
