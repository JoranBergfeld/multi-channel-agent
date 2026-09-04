using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free regression coverage for the EF Core <c>ChangeTracker</c> contamination invariant
/// behind <see cref="SqlImportExecutionStore"/>, mirroring
/// <see cref="SqlReferenceAdministrationStoreChangeTrackerIsolationTests"/>: a real relational engine
/// is used - not a mock and not the InMemory provider, neither of which enforces the filtered
/// uniqueness this depends on - to reproduce, in a single shared
/// <see cref="MultiChannelAgentDbContext"/>, exactly the failure mode production exercises.
///
/// An import stages up to five thousand Stock Entries, its ledger row and its audit fact on the
/// <c>ChangeTracker</c> before a single one of them is saved. One coordinator scope processes a whole
/// batch of requests through one DbContext, so an import that fails on the way to committing must
/// leave nothing staged behind: the very next <c>SaveChangesAsync</c> in that same scope would
/// otherwise flush an entire Inventory's worth of Stock nobody confirmed. The second case below is
/// the one with teeth on that point - it fails at <c>SaveChangesAsync</c>, with everything staged.
///
/// The first case proves the other half, and the half a refused import actually depends on: a
/// conflict is reached before anything is staged, and everything it had already <em>changed</em> -
/// the proposal it consumed, and its file - comes back with it.
///
/// Both invariants are provider-independent, so they are proven here rather than behind a Docker gate.
/// </summary>
public sealed class SqlImportExecutionStoreChangeTrackerIsolationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"u8.ToArray();

    private readonly SqliteConnection _connection;
    private readonly MultiChannelAgentDbContext _db;
    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());
    private readonly LocationId _location = new(Guid.NewGuid());

    public SqlImportExecutionStoreChangeTrackerIsolationTests()
    {
        // An in-memory SQLite database only persists for the lifetime of one open connection, so it is
        // kept open for the whole test and shared by every use below - just as one coordinator scope
        // shares one DbContext, and one connection, across a whole batch of requests in production.
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
    public async Task An_import_into_an_Inventory_that_stopped_being_empty_leaves_nothing_staged_in_the_shared_context()
    {
        SeedExistingStock();
        var proposal = await StorePendingAsync();

        // The Inventory is not empty, so this must refuse - before anything is staged, and after the
        // proposal has already been consumed, which is what has to come back.
        var result = await Store().ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        AssertNothingIsStaged();
        Assert.Single(await _db.StockEntries.AsNoTracking().ToListAsync());
        await AssertNothingWasAppliedAsync(proposal);

        // The very next write in this same scope must not flush what the abandoned import staged.
        await SaveAnUnrelatedChangeAsync();

        Assert.Single(await _db.StockEntries.AsNoTracking().ToListAsync());
        await AssertNothingWasAppliedAsync(proposal);
    }

    [Fact]
    public async Task An_import_whose_entries_cannot_all_be_written_leaves_nothing_staged_in_the_shared_context()
    {
        // Two entries that are Equivalent Stock to each other. The merge refuses to produce this, so
        // reaching the store with it is a fault rather than a race - which is exactly the point: the
        // insert fails after every entry, the ledger row and the audit fact are already staged, and
        // that is the debris this invariant is about.
        var proposal = await StorePendingAsync(Entry("Steel Bolts", 4m), Entry("STEEL BOLTS", 1m));

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => Store().ApplyAsync(Command(proposal), CancellationToken.None));

        AssertNothingIsStaged();
        Assert.Empty(await _db.StockEntries.AsNoTracking().ToListAsync());
        await AssertNothingWasAppliedAsync(proposal);

        await SaveAnUnrelatedChangeAsync();

        // A fault is never laundered into a conflict, and never half-applied either: the whole import
        // is still there to retry, file included.
        Assert.Empty(await _db.StockEntries.AsNoTracking().ToListAsync());
        await AssertNothingWasAppliedAsync(proposal);
    }

    private SqlImportExecutionStore Store() => new(_db);

    private void AssertNothingIsStaged() =>
        Assert.DoesNotContain(_db.ChangeTracker.Entries(), entry => entry.State != EntityState.Unchanged);

    /// <summary>Nothing this task's writer produces may exist, and what it consumes must be untouched.</summary>
    private async Task AssertNothingWasAppliedAsync(ImportProposal proposal)
    {
        Assert.Empty(await _db.ImportOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.InventoryAudits.AsNoTracking().ToListAsync());
        Assert.Equal(
            nameof(ImportProposalStatus.Pending),
            (await _db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
        Assert.Single(await _db.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
    }

    /// <summary>Stands in for the next request's write in the same coordinator scope.</summary>
    private async Task SaveAnUnrelatedChangeAsync()
    {
        _db.Locations.Add(new LocationEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventory.Value,
            Name = "Shelf Z",
            NormalizedName = "shelf z",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<ImportProposal> StorePendingAsync(params ImportEntry[] entries)
    {
        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            _participant,
            _inventory,
            FileDigest.Of(RawContent),
            entries.Length == 0 ? [Entry("Steel Bolts", 4m)] : entries,
            EmptyStateVersion.Empty,
            Now);

        await new SqlImportProposalStore(_db).StoreAsync(proposal, RawContent, Now, CancellationToken.None);
        _db.ChangeTracker.Clear();

        return proposal;
    }

    private ImportExecutionCommand Command(ImportProposal proposal) => new()
    {
        OperationId = proposal.ExecutionOperationId,
        InventoryId = _inventory,
        ActorId = _participant,
        ConsumesProposalId = proposal.Id,
        FileDigest = proposal.FileDigest,
        Entries = proposal.Entries,
        EmptyStateVersion = proposal.EmptyStateVersion,
        Now = Now,
    };

    private ImportEntry Entry(string name, decimal quantity) => new()
    {
        LineNumber = 2,
        SourceLineNumbers = [2],
        Name = name,
        NormalizedName = NameNormalization.Normalize(name),
        Quantity = Quantity.Create(quantity),
        UnitId = _unit,
        UnitCanonicalName = "each",
        LocationId = null,
        LocationName = null,
        Note = null,
    };

    private void SeedExistingStock()
    {
        _db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventory.Value,
            UnitId = _unit.Value,
            Name = "Existing",
            NormalizedName = "existing",

            // A zero-quantity entry is still an entry, so this is exactly the case the gate exists for.
            Quantity = 0m,
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private void Seed()
    {
        _db.Participants.Add(new ParticipantEntity
        {
            Id = _participant.Value,
            DisplayName = "Importing Owner",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        _db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = _participant.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        _db.Units.Add(new UnitEntity
        {
            Id = _unit.Value,
            InventoryId = _inventory.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.Locations.Add(new LocationEntity
        {
            Id = _location.Value,
            InventoryId = _inventory.Value,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }
}
