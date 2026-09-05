using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage of the one seam that publishes "something in this Inventory changed".
///
/// It is deliberately not a call any endpoint or store makes. It keys off the minimal semantic audit
/// fact every state-changing store already stages in the same save, which is what makes it impossible
/// for a future mutation path - or a future channel - to change Inventory state without publishing:
/// forgetting to publish would mean forgetting to audit, which is a far louder failure. Because the
/// bump runs inside the caller's own transaction, and always last, nothing is ever published before
/// it commits, a rollback takes the version with it, and the version row's lock is held for the
/// shortest possible slice of the transaction. It is not a deadlock-prevention scheme and is not
/// claimed to be one.
/// </summary>
public sealed class InventoryVersionBumpTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Actor = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly Guid _inventoryId = Guid.NewGuid();

    public InventoryVersionBumpTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
        Seed(db);
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task A_new_inventory_starts_at_version_zero_without_anyone_asking_for_it()
    {
        using var db = CreateContext();

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task One_audited_change_moves_the_inventory_forward_exactly_one_version()
    {
        using var db = CreateContext();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task Several_audit_facts_committed_together_still_move_it_forward_exactly_once()
    {
        using var db = CreateContext();

        db.InventoryAudits.Add(Audit(AuditEventType.StockAdded));
        db.InventoryAudits.Add(Audit(AuditEventType.StockRemoved));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // The signal is "refetch this Inventory", not "here is a change log", so one commit is one
        // version however many facts it recorded.
        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_denied_access_attempt_changes_nothing_and_therefore_publishes_nothing()
    {
        using var db = CreateContext();

        await RecordAuditAsync(db, AuditEventType.AccessDenied);

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_save_that_records_no_audit_fact_publishes_nothing()
    {
        using var db = CreateContext();

        db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
            RetiredAt = null,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task A_change_that_rolls_back_never_leaves_a_published_version_behind()
    {
        using var db = CreateContext();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await RecordAuditAsync(db, AuditEventType.StockAdded);
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();

        // Nothing was published before the commit, because the bump ran inside the very transaction
        // that was thrown away.
        Assert.Equal(0L, await VersionAsync(db, _inventoryId));
    }

    [Fact]
    public async Task Two_inventories_advance_independently()
    {
        var otherInventoryId = Guid.NewGuid();

        using var db = CreateContext();
        db.Inventories.Add(new InventoryEntity
        {
            Id = otherInventoryId,
            Name = "Other Warehouse",
            NormalizedName = "other warehouse",
            CreatedByParticipantId = Actor.Value,
            ClientRequestId = "seed-2",
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
        Assert.Equal(0L, await VersionAsync(db, otherInventoryId));
    }

    [Fact]
    public async Task The_store_reads_every_requested_inventory_and_omits_the_ones_it_has_no_row_for()
    {
        using var db = CreateContext();
        await RecordAuditAsync(db, AuditEventType.StockAdded);

        var versions = await new SqlInventoryVersionStore(db)
            .ReadAsync([_inventoryId, Guid.NewGuid()], CancellationToken.None);

        Assert.Equal(1L, versions[_inventoryId]);
        Assert.Single(versions);
    }

    [Fact]
    public void The_version_row_is_referentially_independent_of_the_inventory_it_names()
    {
        using var db = CreateContext();

        // Asserted, not assumed. An audit fact about an Inventory deliberately carries no foreign key
        // (see InventoryAuditEntityConfiguration), and this row is published from exactly those facts,
        // so a cascading key here would let a state the audit model tolerates fail somebody else's
        // mutating transaction through the fallback insertion below.
        var entityType = db.Model.FindEntityType(typeof(InventoryVersionEntity))!;

        Assert.Empty(entityType.GetForeignKeys());
        Assert.Equal("InventoryVersions", entityType.GetTableName());
    }

    [Fact]
    public async Task An_inventory_that_somehow_has_no_version_row_gets_one_from_its_next_audited_change()
    {
        using var db = CreateContext();

        // Exactly the residue the migration's backfill exists to prevent, forced here so the guarded
        // fallback is a tested path rather than a hopeful comment. It is reachable at all only because
        // there is no foreign key stopping the row from being established on demand.
        await db.Database.ExecuteSqlAsync($"DELETE FROM InventoryVersions WHERE InventoryId = {_inventoryId}");
        db.ChangeTracker.Clear();

        await RecordAuditAsync(db, AuditEventType.StockAdded);

        Assert.Equal(1L, await VersionAsync(db, _inventoryId));
    }

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);

    private static async Task<long> VersionAsync(MultiChannelAgentDbContext db, Guid inventoryId)
    {
        var row = await db.InventoryVersions.AsNoTracking().FirstOrDefaultAsync(v => v.InventoryId == inventoryId);
        return row?.Version ?? -1L;
    }

    private async Task RecordAuditAsync(MultiChannelAgentDbContext db, AuditEventType eventType)
    {
        db.InventoryAudits.Add(Audit(eventType));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private InventoryAuditEntity Audit(AuditEventType eventType) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType.ToString(),
        ActorKind = AuditActorKind.Participant.ToString(),
        ActorId = Actor.Value.ToString(),
        InventoryId = _inventoryId,
        SubjectParticipantId = null,
        OutcomeCode = "ok",
        OccurredAtUtc = Now,
        OccurredAtUtcTicks = Now.UtcTicks,
        ExpiresAtUtc = Now.AddDays(90),
    };

    private void Seed(MultiChannelAgentDbContext db)
    {
        db.Participants.Add(new ParticipantEntity
        {
            Id = Actor.Value,
            DisplayName = "Version Participant",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = Actor.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }
}
