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
/// behind <see cref="SqlReferenceAdministrationStore"/>, mirroring
/// <see cref="SqlStockChangeSetStoreChangeTrackerIsolationTests"/>: a real relational engine is used -
/// not a mock and not the InMemory provider, neither of which enforces the filtered uniqueness this
/// depends on - to reproduce, in a single shared <see cref="MultiChannelAgentDbContext"/>, exactly the
/// failure mode production exercises.
///
/// A reference change set stages its inserts - a created Unit, its terms, a created Location, the
/// audits, and the ledger - on the <c>ChangeTracker</c> before later guarded statements run. One
/// coordinator scope processes a whole batch of Turns through one DbContext, so a change set that
/// ends in a conflict must leave nothing staged behind: the very next Turn's
/// <c>SaveChangesAsync</c> in that same scope would otherwise flush reference data nobody asked for.
///
/// The invariant is provider-independent, so it is proven here rather than behind a Docker gate.
/// </summary>
public sealed class SqlReferenceAdministrationStoreChangeTrackerIsolationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly MultiChannelAgentDbContext _db;
    private readonly Guid _inventoryId = Guid.NewGuid();
    private readonly Guid _eachUnitId = Guid.NewGuid();
    private readonly ParticipantId _actorId = new(Guid.NewGuid());

    public SqlReferenceAdministrationStoreChangeTrackerIsolationTests()
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
    public async Task A_change_set_whose_term_was_already_claimed_leaves_nothing_staged_in_the_shared_context()
    {
        SeedUnit("Carton");
        var store = Store();

        var result = await store.ApplyAsync(
            Command(
                [
                    CreateUnitChange(order: 1, "Carton"),
                    CreateLocationChange(order: 2, "Shelf A"),
                ],
                absences: [new ExpectedTermAbsence(ReferenceKind.Unit, "carton")]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);
        AssertNothingIsStaged();
        await AssertNothingWasAppliedAsync();

        // The very next write in this same scope must not flush what the abandoned set staged.
        await SaveAnUnrelatedChangeAsync();
        await AssertNothingWasAppliedAsync();
    }

    [Fact]
    public async Task A_change_set_whose_version_moved_leaves_nothing_staged_in_the_shared_context()
    {
        var cartonId = SeedUnit("Carton");
        var store = Store();

        // A create is staged first, and only then does the guarded version check for the rename find a
        // stamp nobody holds any more - so the staged create is exactly what must not survive.
        var result = await store.ApplyAsync(
            Command(
                [
                    CreateLocationChange(order: 1, "Shelf A"),
                    RenameUnitChange(order: 2, cartonId, "Crate"),
                ],
                versions: [new ExpectedReferenceVersion(ReferenceKind.Unit, cartonId, Guid.NewGuid())]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);
        AssertNothingIsStaged();
        Assert.Equal("Carton", (await _db.Units.AsNoTracking().SingleAsync(u => u.Id == cartonId)).CanonicalName);
        await AssertNothingWasAppliedAsync();

        await SaveAnUnrelatedChangeAsync();
        await AssertNothingWasAppliedAsync();
    }

    [Fact]
    public async Task A_Retire_that_Stock_still_references_leaves_nothing_staged_in_the_shared_context()
    {
        var cartonId = SeedUnit("Carton");
        SeedStock(cartonId);
        var store = Store();

        var result = await store.ApplyAsync(
            Command(
                [
                    CreateLocationChange(order: 1, "Shelf A"),
                    RetireUnitChange(order: 2, cartonId),
                ],
                versions: [new ExpectedReferenceVersion(ReferenceKind.Unit, cartonId, CurrentStampOf(cartonId))]),
            CancellationToken.None);

        Assert.Equal(ReferenceAdministrationStoreOutcome.Conflict, result.Outcome);
        AssertNothingIsStaged();
        Assert.Null((await _db.Units.AsNoTracking().SingleAsync(u => u.Id == cartonId)).RetiredAt);
        await AssertNothingWasAppliedAsync();

        await SaveAnUnrelatedChangeAsync();
        await AssertNothingWasAppliedAsync();
    }

    private SqlReferenceAdministrationStore Store() => new(_db, new SqlConfirmationProposalStore(_db));

    private void AssertNothingIsStaged() =>
        Assert.DoesNotContain(_db.ChangeTracker.Entries(), entry => entry.State != EntityState.Unchanged);

    /// <summary>Nothing this ticket's writer produces may exist: no new Location, no ledger, no audit.</summary>
    private async Task AssertNothingWasAppliedAsync()
    {
        Assert.Empty(await _db.Locations.AsNoTracking().Where(l => l.NormalizedName == "shelf a").ToListAsync());
        Assert.Empty(await _db.ReferenceOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.ReferenceEffects.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InventoryAudits.AsNoTracking().ToListAsync());
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
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private ReferenceChangeSetCommand Command(
        IReadOnlyList<ProposedReferenceChange> changes,
        IReadOnlyList<ExpectedReferenceVersion>? versions = null,
        IReadOnlyList<ExpectedTermAbsence>? absences = null) => new()
        {
            OperationId = new ReferenceOperationId(Guid.NewGuid()),
            InventoryId = new InventoryId(_inventoryId),
            ActorId = _actorId,
            ConfirmedByTurnId = TurnId.NewId(),
            ConsumesProposalId = null,
            Changes = changes,
            ExpectedVersions = versions ?? [],
            ExpectedTermAbsences = absences ?? [],
            Now = Now,
        };

    private static ProposedReferenceChange CreateUnitChange(int order, string name) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.CreateUnit,
        Target = new ProposedReferenceState(
            ReferenceKind.Unit, Guid.NewGuid(), name, NameNormalization.Normalize(name), Reserved: false),
        Terms = [UnitTerm.Create(name, isCanonical: true, isReserved: false)],
    };

    private static ProposedReferenceChange CreateLocationChange(int order, string name) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.CreateLocation,
        Target = new ProposedReferenceState(
            ReferenceKind.Location, Guid.NewGuid(), name, NameNormalization.Normalize(name), Reserved: false),
    };

    private static ProposedReferenceChange RenameUnitChange(int order, Guid unitId, string newName) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.RenameUnit,
        Target = new ProposedReferenceState(ReferenceKind.Unit, unitId, "Carton", "carton", Reserved: false),
        NewName = newName,
        NewNormalizedName = NameNormalization.Normalize(newName),
    };

    private static ProposedReferenceChange RetireUnitChange(int order, Guid unitId) => new()
    {
        Order = order,
        Kind = ReferenceChangeKind.RetireUnit,
        Target = new ProposedReferenceState(ReferenceKind.Unit, unitId, "Carton", "carton", Reserved: false),
    };

    private Guid CurrentStampOf(Guid unitId) => _db.Units.AsNoTracking().Single(u => u.Id == unitId).ConcurrencyStamp;

    private Guid SeedUnit(string canonicalName)
    {
        var unitId = Guid.NewGuid();

        _db.Units.Add(new UnitEntity
        {
            Id = unitId,
            InventoryId = _inventoryId,
            CanonicalName = canonicalName,
            NormalizedCanonicalName = NameNormalization.Normalize(canonicalName),
            IsReserved = false,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            UnitId = unitId,
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

    private void SeedStock(Guid unitId)
    {
        _db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            UnitId = unitId,
            Name = "Steel Bolts",
            NormalizedName = "steel bolts",
            Quantity = 1m,
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
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
            Id = _eachUnitId,
            InventoryId = _inventoryId,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.UnitTerms.Add(new UnitTermEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventoryId,
            UnitId = _eachUnitId,
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
