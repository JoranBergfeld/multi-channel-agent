using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free regression coverage for the EF Core <c>ChangeTracker</c> contamination invariant
/// behind <see cref="SqlStockChangeSetStore"/>, mirroring
/// <see cref="SqlTurnResultStoreChangeTrackerIsolationTests"/>: a real relational engine is used - not
/// a mock and not the InMemory provider, neither of which enforces the uniqueness this depends on -
/// to reproduce, in a single shared <see cref="MultiChannelAgentDbContext"/>, exactly the failure mode
/// production exercises.
///
/// A change set stages its inserts (<c>Created</c>, and a <c>Split</c>'s destination) on the
/// <c>ChangeTracker</c> before later guarded statements run. One coordinator scope processes a whole
/// batch of Turns through one DbContext, so a change set that ends in a conflict - or in a raw
/// provider fault - must leave nothing staged behind: the very next Turn's
/// <c>SaveChangesAsync</c> in that same scope would otherwise flush Stock Entries nobody confirmed.
/// </summary>
public sealed class SqlStockChangeSetStoreChangeTrackerIsolationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly MultiChannelAgentDbContext _db;
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _unitId = Guid.NewGuid();
    private readonly ParticipantId _actorId = new(Guid.NewGuid());

    public SqlStockChangeSetStoreChangeTrackerIsolationTests()
    {
        // An in-memory SQLite database only persists for the lifetime of one open connection, so it is
        // kept open for the whole test and shared by every use below - just as one coordinator scope
        // shares one DbContext, and one connection, across a whole batch of Turns in production.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new MultiChannelAgentDbContext(
            new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        Seed();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_change_set_that_conflicts_after_staging_a_create_leaves_nothing_behind_in_the_shared_context()
    {
        var store = new SqlStockChangeSetStore(_db);

        // Order 1 stages an insert; order 2 then finds nothing to delete, which is exactly the guarded
        // "affected no row" branch the store answers as a conflict.
        var command = Command(
        [
            CreateChange(order: 1, "Copper Nails", 4m),
            ForgetChange(order: 2, new StockEntryId(Guid.NewGuid())),
        ]);

        var result = await store.ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);
        await AssertNothingWasAppliedAsync();
        AssertNothingIsStaged();

        // The very next write in this same scope must not flush the create that conflict abandoned.
        await SaveAnUnrelatedChangeAsync();
        await AssertNothingWasAppliedAsync();
    }

    [Fact]
    public async Task A_change_set_that_faults_after_staging_a_create_leaves_nothing_behind_in_the_shared_context()
    {
        var occupied = SeedStock("Brass Rivets", 6m);
        var source = SeedStock("Steel Bolts", 4m);
        var store = new SqlStockChangeSetStore(_db);

        // Renaming onto an occupied Equivalent Stock key violates the uniqueness index from inside a
        // guarded ExecuteUpdate, which the provider raises directly rather than as a DbUpdateException.
        // Nothing pinned that key as expected-absent, so there is no evidence this was a losing race:
        // the fault must propagate rather than be laundered into a clean conflict.
        var command = Command(
        [
            CreateChange(order: 1, "Copper Nails", 4m),
            RenameChange(order: 2, source, "Brass Rivets"),
        ]);

        await Assert.ThrowsAnyAsync<Exception>(() => store.ApplyAsync(command, CancellationToken.None));

        AssertNothingIsStaged();
        Assert.Equal(2, await _db.StockEntries.AsNoTracking().CountAsync());
        Assert.Equal("Steel Bolts", (await _db.StockEntries.AsNoTracking().SingleAsync(e => e.Id == source)).Name);
        Assert.Equal(6m, (await _db.StockEntries.AsNoTracking().SingleAsync(e => e.Id == occupied)).Quantity);
        Assert.Empty(_db.InventoryAudits.AsNoTracking());
        Assert.Empty(_db.StockChangeSetOperations.AsNoTracking());

        // And the next Turn's write in this scope still sees only what was really there.
        await SaveAnUnrelatedChangeAsync();
        Assert.Equal(2, await _db.StockEntries.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task A_change_set_whose_expected_absence_was_filled_still_answers_a_clean_conflict()
    {
        var source = SeedStock("Steel Bolts", 4m);
        SeedStock("Brass Rivets", 6m);
        var store = new SqlStockChangeSetStore(_db);

        // The same collision as above, but this time the caller pinned the key it expected to be free.
        // That is exact evidence of a losing race, so it settles as a conflict rather than a fault.
        var command = Command(
            [RenameChange(order: 1, source, "Brass Rivets")],
            absences: [new ExpectedEquivalentStockAbsence("brass rivets", new UnitId(_unitId), null)]);

        var result = await store.ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Conflict, result.Outcome);
        AssertNothingIsStaged();
        Assert.Equal("Steel Bolts", (await _db.StockEntries.AsNoTracking().SingleAsync(e => e.Id == source)).Name);
        Assert.Empty(_db.StockChangeSetOperations.AsNoTracking());
    }

    private void AssertNothingIsStaged() =>
        Assert.DoesNotContain(_db.ChangeTracker.Entries(), entry => entry.State != EntityState.Unchanged);

    private async Task AssertNothingWasAppliedAsync()
    {
        Assert.Empty(_db.StockEntries.AsNoTracking());
        Assert.Empty(_db.InventoryAudits.AsNoTracking());
        Assert.Empty(_db.StockChangeSetOperations.AsNoTracking());
        Assert.Empty(_db.StockChangeSetEffects.AsNoTracking());
        await Task.CompletedTask;
    }

    /// <summary>Stands in for the next Turn's result write in the same coordinator scope.</summary>
    private async Task SaveAnUnrelatedChangeAsync()
    {
        _db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            Name = "Shelf Z",
            NormalizedName = "shelf z",
            CreatedAt = Now,
        });

        await _db.SaveChangesAsync();
    }

    private StockChangeSetCommand Command(
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEquivalentStockAbsence>? absences = null) => new()
        {
            OperationId = new StockOperationId(Guid.NewGuid()),
            InventoryId = new InventoryId(_inventoryId),
            ActorId = _actorId,
            ConfirmedByTurnId = TurnId.NewId(),
            ConsumesProposalId = null,
            Changes = changes,

            // Deliberately unpinned: these tests drive the store's own defensive branches, which must
            // hold whatever a caller did or did not pin.
            ExpectedVersions = [],
            ExpectedAbsences = absences ?? [],
            Now = Now,
        };

    private ProposedChange CreateChange(int order, string name, decimal quantity) => new()
    {
        Order = order,
        Kind = StockMutationKind.Add,
        Effect = StockChangeEffectKind.Created,
        Source = State(null, name, 0m, quantity),
    };

    private ProposedChange ForgetChange(int order, StockEntryId stockEntryId) => new()
    {
        Order = order,
        Kind = StockMutationKind.Forget,
        Effect = StockChangeEffectKind.Forgotten,
        Source = State(stockEntryId, "Ghost Entry", 0m, 0m, retired: true),
    };

    private ProposedChange RenameChange(int order, Guid stockEntryId, string newName) => new()
    {
        Order = order,
        Kind = StockMutationKind.Rename,
        Effect = StockChangeEffectKind.Renamed,
        Source = State(new StockEntryId(stockEntryId), "Steel Bolts", 4m, 4m),
        NewName = newName,
        NewNormalizedName = NameNormalization.Normalize(newName),
    };

    private ProposedEntryState State(
        StockEntryId? stockEntryId, string name, decimal previous, decimal resulting, bool retired = false) => new(
        stockEntryId,
        name,
        NameNormalization.Normalize(name),
        new UnitId(_unitId),
        "each",
        LocationId: null,
        LocationName: null,
        Note: null,
        Quantity.Create(previous),
        Quantity.Create(resulting),
        retired);

    private Guid SeedStock(string name, decimal quantity)
    {
        var stockEntryId = Guid.NewGuid();
        _db.StockEntries.Add(new StockEntryEntity
        {
            Id = stockEntryId,
            InventoryId = _inventoryId,
            UnitId = _unitId,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        return stockEntryId;
    }

    private void Seed()
    {
        var participantId = Guid.NewGuid();
        _db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        _db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        _db.Units.Add(new UnitEntity
        {
            Id = _unitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }
}
