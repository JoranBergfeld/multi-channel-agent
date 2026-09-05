using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
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

    public DbSet<TurnProgressEventEntity> TurnProgressEvents => Set<TurnProgressEventEntity>();

    public DbSet<LeaseEntity> Leases => Set<LeaseEntity>();

    public DbSet<ParticipantEntity> Participants => Set<ParticipantEntity>();

    public DbSet<InventoryEntity> Inventories => Set<InventoryEntity>();

    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();

    public DbSet<UnitEntity> Units => Set<UnitEntity>();

    public DbSet<UnitTermEntity> UnitTerms => Set<UnitTermEntity>();

    public DbSet<ActiveInventorySelectionEntity> ActiveInventorySelections => Set<ActiveInventorySelectionEntity>();

    public DbSet<AuthTicketEntity> AuthTickets => Set<AuthTicketEntity>();

    public DbSet<InventoryAuditEntity> InventoryAudits => Set<InventoryAuditEntity>();

    public DbSet<InventoryVersionEntity> InventoryVersions => Set<InventoryVersionEntity>();

    public DbSet<LocationEntity> Locations => Set<LocationEntity>();

    public DbSet<StockEntryEntity> StockEntries => Set<StockEntryEntity>();

    public DbSet<StockOperationEntity> StockOperations => Set<StockOperationEntity>();

    public DbSet<ConfirmationProposalEntity> ConfirmationProposals => Set<ConfirmationProposalEntity>();

    public DbSet<ConfirmationProposalReferenceEntity> ConfirmationProposalReferences => Set<ConfirmationProposalReferenceEntity>();

    public DbSet<ReferenceOperationEntity> ReferenceOperations => Set<ReferenceOperationEntity>();

    public DbSet<ReferenceEffectEntity> ReferenceEffects => Set<ReferenceEffectEntity>();

    public DbSet<StockChangeSetOperationEntity> StockChangeSetOperations => Set<StockChangeSetOperationEntity>();

    public DbSet<StockChangeSetEffectEntity> StockChangeSetEffects => Set<StockChangeSetEffectEntity>();

    public DbSet<ImportProposalEntity> ImportProposals => Set<ImportProposalEntity>();

    public DbSet<ImportUploadEntity> ImportUploads => Set<ImportUploadEntity>();

    public DbSet<ImportOperationEntity> ImportOperations => Set<ImportOperationEntity>();

    public DbSet<FoundryConversationBindingEntity> FoundryConversationBindings => Set<FoundryConversationBindingEntity>();

    /// <summary>
    /// The columns Stock and reference ordering, keyset pagination, and uniqueness compare
    /// (<see cref="Domain.Inventories.StockEntryOrderKey"/> and
    /// <see cref="Domain.Inventories.ReferenceOrderKey"/>). They must compare exactly as the domain
    /// does - ordinally - so the database's order is the domain's order rather than a locale-dependent
    /// approximation of it.
    ///
    /// <c>UnitTerms.NormalizedTerm</c> belongs here for both reasons at once: the shared Unit term
    /// namespace's filtered unique index is enforced against it, and bounded suggestions order by it.
    /// Left on a default collation it would make the namespace accent-insensitive on SQL Server and
    /// accent-sensitive on SQLite, and order suggestions differently on each.
    /// </summary>
    private static readonly (Type EntityType, string PropertyName)[] OrdinalOrderKeyColumns =
    [
        (typeof(StockEntryEntity), nameof(StockEntryEntity.NormalizedName)),
        (typeof(UnitEntity), nameof(UnitEntity.NormalizedCanonicalName)),
        (typeof(UnitTermEntity), nameof(UnitTermEntity.NormalizedTerm)),
        (typeof(LocationEntity), nameof(LocationEntity.NormalizedName)),
    ];

    /// <summary>
    /// SQL Server's default collations compare text by locale rules (case- and accent-insensitive,
    /// with locale-specific ordering), which would order and compare the normalized order keys
    /// differently from the domain. A binary collation makes the comparison ordinal, exactly like
    /// <see cref="string.CompareOrdinal"/>. SQLite already compares text binarily by default, so it
    /// needs nothing here.
    /// </summary>
    internal const string OrdinalSqlServerCollation = "Latin1_General_100_BIN2";

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

    /// <summary>
    /// The one place this application publishes "something in this Inventory changed".
    ///
    /// It is a save-time seam rather than a call each store makes, because a call each store makes is
    /// a call a future store can forget. Every write that changes Inventory-visible state already
    /// stages a minimal semantic <see cref="InventoryAuditEntity"/> in the same save, so keying off
    /// that means forgetting to publish would require forgetting to audit - a far louder failure, and
    /// one the governance tests already catch. <see cref="AuditEventType.AccessDenied"/> is excluded
    /// because a refused request changes nothing there is anything to refetch for.
    ///
    /// The bump runs INSIDE the caller's transaction, and always LAST. Inside, so nothing is ever
    /// published before the change it announces commits, and a rollback takes the version with it.
    /// Last, so the version row's exclusive lock is taken as late as possible and released at commit -
    /// the shortest hold this design can have. That is commit coupling and a short lock, and it is
    /// deliberately NOT claimed to be anything more: it does not serialize the work earlier in those
    /// transactions, and it prevents no deadlock that could already happen on the rows they were
    /// changing. No writer takes this lock first, and nothing here depends on one doing so.
    /// </summary>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StageVersionRowsForNewInventories();

        var inventoriesToPublish = InventoriesWithStagedChanges();
        if (inventoriesToPublish.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var ownedTransaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            foreach (var inventoryId in inventoriesToPublish)
            {
                var bumped = await Database.ExecuteSqlAsync(
                    $"UPDATE InventoryVersions SET Version = Version + 1 WHERE InventoryId = {inventoryId}",
                    cancellationToken);

                if (bumped == 0)
                {
                    // Only reachable for an Inventory created before this table existed and somehow
                    // missed by the backfill. Establishing the row here keeps the signal correct
                    // rather than silently never publishing for that Inventory again.
                    await Database.ExecuteSqlAsync(
                        $"INSERT INTO InventoryVersions (InventoryId, Version) VALUES ({inventoryId}, 1)",
                        cancellationToken);
                }
            }

            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            return saved;
        }
        catch
        {
            // The staged entities were either never written or have been rolled back with the
            // transaction, so leaving them tracked would resend them on the next save against this
            // same context - which one processing pass shares across a whole batch of work.
            ChangeTracker.Clear();
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The synchronous twin of <see cref="SaveChangesAsync(bool, CancellationToken)"/>. It exists so
    /// the publish seam cannot be bypassed simply by saving synchronously; production code saves
    /// asynchronously, but a seam with a hole in it is not a seam.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StageVersionRowsForNewInventories();

        var inventoriesToPublish = InventoriesWithStagedChanges();
        if (inventoriesToPublish.Count == 0)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        var ownedTransaction = Database.CurrentTransaction is null ? Database.BeginTransaction() : null;

        try
        {
            var saved = base.SaveChanges(acceptAllChangesOnSuccess);

            foreach (var inventoryId in inventoriesToPublish)
            {
                var bumped = Database.ExecuteSql(
                    $"UPDATE InventoryVersions SET Version = Version + 1 WHERE InventoryId = {inventoryId}");

                if (bumped == 0)
                {
                    Database.ExecuteSql(
                        $"INSERT INTO InventoryVersions (InventoryId, Version) VALUES ({inventoryId}, 1)");
                }
            }

            ownedTransaction?.Commit();

            return saved;
        }
        catch
        {
            ChangeTracker.Clear();
            throw;
        }
        finally
        {
            ownedTransaction?.Dispose();
        }
    }

    /// <summary>
    /// Gives every Inventory being created its starting version in the same save, so the bump above
    /// is always an update of a row that exists. An Inventory's own creation writes no audit fact, so
    /// it starts at zero and is first reported the moment it appears in a Participant's authorized
    /// set.
    /// </summary>
    private void StageVersionRowsForNewInventories()
    {
        var newInventoryIds = ChangeTracker.Entries<InventoryEntity>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity.Id)
            .ToList();

        if (newInventoryIds.Count == 0)
        {
            return;
        }

        var alreadyStaged = ChangeTracker.Entries<InventoryVersionEntity>()
            .Select(entry => entry.Entity.InventoryId)
            .ToHashSet();

        foreach (var inventoryId in newInventoryIds.Where(id => !alreadyStaged.Contains(id)))
        {
            InventoryVersions.Add(new InventoryVersionEntity { InventoryId = inventoryId, Version = 0L });
        }
    }

    private List<Guid> InventoriesWithStagedChanges() =>
        ChangeTracker.Entries<InventoryAuditEntity>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => entry.Entity)
            .Where(audit => audit.EventType != nameof(AuditEventType.AccessDenied))
            .Select(audit => audit.InventoryId)
            .Distinct()
            .ToList();
}
