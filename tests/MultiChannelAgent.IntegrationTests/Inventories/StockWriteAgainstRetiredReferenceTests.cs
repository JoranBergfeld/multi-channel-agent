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
/// Issue #33's sixth acceptance criterion from the Stock side: a confirmed Retire must leave no way
/// for Stock to end up referencing the reference it retired.
///
/// The dangerous window is not a thread race - it is the ordinary shape of every Stock write. The
/// Application layer resolves a Unit or Location (active-only) while planning, and the store writes
/// some time later. A Retire that commits in between would leave the write holding a decision that
/// is no longer true, and nothing downstream re-checks it: the Stock stores pin Stock Entry versions,
/// never reference retirement.
///
/// These tests reproduce that window exactly, and deterministically, by doing what production does in
/// the order production does it: resolve, then retire, then write. No barriers and no chance - if the
/// write can still land, it lands every single time.
/// </summary>
public sealed class StockWriteAgainstRetiredReferenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly MultiChannelAgentDbContext _db;
    private readonly InventoryId _inventoryId = new(Guid.NewGuid());
    private readonly ParticipantId _actorId = new(Guid.NewGuid());
    private readonly UnitId _eachUnitId = new(Guid.NewGuid());

    public StockWriteAgainstRetiredReferenceTests()
    {
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
    public async Task A_direct_Stock_create_never_lands_on_a_Unit_retired_after_it_was_resolved()
    {
        var boxId = SeedUnit("Cardboard Box");

        // What the Application layer does while planning: resolve the Unit, active-only.
        var resolved = await Resolve().ResolveUnitAsync(_inventoryId, "Cardboard Box", CancellationToken.None);
        Assert.Equal(boxId, resolved);

        await RetireUnitAsync(boxId);

        var result = await new SqlStockMutationStore(_db).ApplyAsync(
            CreateCommand("Steel Bolts", boxId, locationId: null), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.StateChanged, result.Outcome);
        await AssertNoStockAndNoLedgerAsync();
    }

    [Fact]
    public async Task A_direct_Stock_create_never_lands_in_a_Location_retired_after_it_was_resolved()
    {
        var shelfId = SeedLocation("Shelf A");

        var resolved = await Resolve().ResolveLocationAsync(_inventoryId, "Shelf A", CancellationToken.None);
        Assert.Equal(shelfId, resolved);

        await RetireLocationAsync(shelfId);

        var result = await new SqlStockMutationStore(_db).ApplyAsync(
            CreateCommand("Steel Bolts", _eachUnitId, shelfId), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.StateChanged, result.Outcome);
        await AssertNoStockAndNoLedgerAsync();
    }

    [Fact]
    public async Task A_confirmed_change_set_never_creates_Stock_at_a_Location_retired_after_it_was_proposed()
    {
        var shelfId = SeedLocation("Shelf A");

        var resolved = await Resolve().ResolveLocationAsync(_inventoryId, "Shelf A", CancellationToken.None);
        Assert.Equal(shelfId, resolved);

        await RetireLocationAsync(shelfId);

        var result = await new SqlStockChangeSetStore(_db).ApplyAsync(
            ChangeSetCommand(CreateChange("Steel Bolts", _eachUnitId, shelfId)), CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);
        await AssertNoStockAndNoLedgerAsync();
    }

    [Fact]
    public async Task A_confirmed_change_set_never_places_existing_Stock_in_a_Location_retired_after_it_was_proposed()
    {
        var shelfId = SeedLocation("Shelf A");
        var (stockEntryId, stamp) = SeedStock("Steel Bolts", _eachUnitId, locationId: null, quantity: 4m);

        await RetireLocationAsync(shelfId);

        var result = await new SqlStockChangeSetStore(_db).ApplyAsync(
            ChangeSetCommand(
                PlaceChange(stockEntryId, "Steel Bolts", _eachUnitId, shelfId),
                [new ExpectedEntryVersion(stockEntryId, stamp)]),
            CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Conflict, result.Outcome);

        // The Stock Entry stayed exactly where it was, and nothing was recorded.
        var entry = await _db.StockEntries.AsNoTracking().SingleAsync(e => e.Id == stockEntryId.Value);
        Assert.Null(entry.LocationId);
        Assert.Empty(await _db.StockChangeSetOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InventoryAudits.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_confirmed_change_set_whose_reference_went_stale_leaves_its_proposal_pending()
    {
        var shelfId = SeedLocation("Shelf A");
        var proposalId = await SeedPendingProposalAsync();

        await RetireLocationAsync(shelfId);

        var result = await new SqlStockChangeSetStore(_db).ApplyAsync(
            ChangeSetCommand(CreateChange("Steel Bolts", _eachUnitId, shelfId), consumesProposalId: proposalId),
            CancellationToken.None);

        Assert.Equal(StockChangeSetStoreOutcome.Conflict, result.Outcome);

        // Rolled back with everything else: the proposal was never consumed, so the caller settles it
        // as Conflicted itself rather than the store silently burning it.
        var proposal = await _db.ConfirmationProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposalId.Value);
        Assert.Equal(nameof(ProposalStatus.Pending), proposal.Status);
        await AssertNoStockAndNoLedgerAsync();
    }

    [Fact]
    public async Task An_ordinary_Stock_write_against_an_active_reference_is_untouched()
    {
        var shelfId = SeedLocation("Shelf A");

        var result = await new SqlStockMutationStore(_db).ApplyAsync(
            CreateCommand("Steel Bolts", _eachUnitId, shelfId), CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.Applied, result.Outcome);
        Assert.Equal(1, await _db.StockEntries.AsNoTracking().CountAsync());
        Assert.Equal(1, await _db.StockOperations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task A_replayed_operation_still_re_reports_itself_after_its_reference_is_retired()
    {
        var boxId = SeedUnit("Cardboard Box");
        var command = CreateCommand("Steel Bolts", boxId, locationId: null);

        var first = await new SqlStockMutationStore(_db).ApplyAsync(command, CancellationToken.None);
        Assert.Equal(StockMutationStoreOutcome.Applied, first.Outcome);

        // Retiring is blocked while Stock references it, so this is only reachable by going around the
        // administration store - which is exactly why replay must not depend on the reference at all.
        await RetireUnitAsync(boxId);

        var replay = await new SqlStockMutationStore(_db).ApplyAsync(command, CancellationToken.None);

        Assert.Equal(StockMutationStoreOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(first.Recorded!.StockEntryId, replay.Recorded!.StockEntryId);
        Assert.Equal(1, await _db.StockEntries.AsNoTracking().CountAsync());
    }

    private SqlInventoryReferenceStore Resolve() => new(_db);

    private async Task AssertNoStockAndNoLedgerAsync()
    {
        Assert.Empty(await _db.StockEntries.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.StockOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.StockChangeSetOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InventoryAudits.AsNoTracking().ToListAsync());
    }

    private StockMutationCommand CreateCommand(string name, UnitId unitId, LocationId? locationId) => new()
    {
        OperationId = StockOperationId.Derive(TurnId.NewId(), "add_stock", 0),
        InventoryId = _inventoryId,
        ActorId = _actorId,
        Kind = StockMutationKind.Add,
        Amount = Quantity.Create(4m),
        ResultingQuantity = Quantity.Create(4m),
        NewEntryName = name,
        NewEntryUnitId = unitId,
        NewEntryLocationId = locationId,
        NotePreserved = false,
        Now = Now,
    };

    private StockChangeSetCommand ChangeSetCommand(
        ProposedChange change,
        IReadOnlyList<ExpectedEntryVersion>? versions = null,
        ProposalId? consumesProposalId = null) => new()
        {
            OperationId = new StockOperationId(Guid.NewGuid()),
            InventoryId = _inventoryId,
            ActorId = _actorId,
            ConfirmedByTurnId = TurnId.NewId(),
            ConsumesProposalId = consumesProposalId,
            Changes = [change],
            ExpectedVersions = versions ?? [],
            ExpectedAbsences = [],
            Now = Now,
        };

    private static ProposedChange CreateChange(string name, UnitId unitId, LocationId locationId) => new()
    {
        Order = 1,
        Kind = StockMutationKind.Add,
        Effect = StockChangeEffectKind.Created,
        Source = State(null, name, unitId, locationId, Quantity.Zero, Quantity.Create(4m)),
    };

    private static ProposedChange PlaceChange(StockEntryId stockEntryId, string name, UnitId unitId, LocationId locationId) => new()
    {
        Order = 1,
        Kind = StockMutationKind.Move,
        Effect = StockChangeEffectKind.Placed,
        Source = State(stockEntryId, name, unitId, null, Quantity.Create(4m), Quantity.Create(4m)),
        Destination = State(stockEntryId, name, unitId, locationId, Quantity.Create(4m), Quantity.Create(4m)),
        TransferredQuantity = Quantity.Create(4m),
    };

    private static ProposedEntryState State(
        StockEntryId? stockEntryId,
        string name,
        UnitId unitId,
        LocationId? locationId,
        Quantity previous,
        Quantity resulting) => new(
        stockEntryId,
        name,
        NameNormalization.Normalize(name),
        unitId,
        "each",
        locationId,
        locationId is null ? null : "Shelf A",
        Note: null,
        previous,
        resulting,
        Retired: false);

    private async Task<ProposalId> SeedPendingProposalAsync()
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());
        var proposal = ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            _actorId,
            "web:profile-1",
            _inventoryId,
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = State(stockEntryId, "Ghost", _eachUnitId, null, Quantity.Zero, Quantity.Zero),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);

        await new SqlConfirmationProposalStore(_db).StoreAsync(proposal, Now, CancellationToken.None);
        _db.ChangeTracker.Clear();

        return proposal.Id;
    }

    private UnitId SeedUnit(string canonicalName)
    {
        var unitId = new UnitId(Guid.NewGuid());

        _db.Units.Add(new UnitEntity
        {
            Id = unitId.Value,
            InventoryId = _inventoryId.Value,
            CanonicalName = canonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(canonicalName),
            IsReserved = false,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId.Value,
            UnitId = unitId.Value,
            Term = canonicalName,
            NormalizedTerm = NameNormalization.Normalize(canonicalName),
            IsCanonical = true,
            IsReserved = false,
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        return unitId;
    }

    private LocationId SeedLocation(string name)
    {
        var locationId = new LocationId(Guid.NewGuid());

        _db.Locations.Add(new LocationEntity
        {
            Id = locationId.Value,
            InventoryId = _inventoryId.Value,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        return locationId;
    }

    private (StockEntryId Id, Guid Stamp) SeedStock(string name, UnitId unitId, LocationId? locationId, decimal quantity)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());
        var stamp = Guid.NewGuid();

        _db.StockEntries.Add(new StockEntryEntity
        {
            Id = stockEntryId.Value,
            InventoryId = _inventoryId.Value,
            UnitId = unitId.Value,
            LocationId = locationId?.Value,
            Name = name,
            NormalizedName = NameNormalization.Normalize(name),
            Quantity = quantity,
            ConcurrencyStamp = stamp,
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        return (stockEntryId, stamp);
    }

    /// <summary>Retires a Unit the way the administration store does: the Unit and every one of its terms.</summary>
    private async Task RetireUnitAsync(UnitId unitId)
    {
        await _db.Units.Where(u => u.Id == unitId.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.RetiredAt, Now));
        await _db.UnitTerms.Where(t => t.UnitId == unitId.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RetiredAt, Now));
        _db.ChangeTracker.Clear();
    }

    private async Task RetireLocationAsync(LocationId locationId)
    {
        await _db.Locations.Where(l => l.Id == locationId.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.RetiredAt, Now));
        _db.ChangeTracker.Clear();
    }

    private void Seed()
    {
        var participantId = _actorId.Value;

        _db.Participants.Add(new ParticipantEntity
        {
            Id = participantId,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        _db.Inventories.Add(new InventoryEntity
        {
            Id = _inventoryId.Value,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = participantId,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        _db.Units.Add(new UnitEntity
        {
            Id = _eachUnitId.Value,
            InventoryId = _inventoryId.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId.Value,
            UnitId = _eachUnitId.Value,
            Term = "each",
            NormalizedTerm = "each",
            IsCanonical = true,
            IsReserved = true,
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }
}
