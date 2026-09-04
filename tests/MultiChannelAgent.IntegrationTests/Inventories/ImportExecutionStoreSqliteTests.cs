using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Docker-free coverage of everything <see cref="SqlImportExecutionStore"/> guarantees that does not
/// depend on SQL Server's locking, in the shape <see cref="ImportProposalStoreSqliteTests"/>
/// established: one shared-cache in-memory SQLite database, a fresh
/// <see cref="MultiChannelAgentDbContext"/> per store, and a real relational engine rather than a
/// double - the Equivalent Stock filtered unique indexes and the foreign keys are part of what is
/// being proved.
///
/// It exists because <see cref="SqlImportExecutionStoreTests"/> - the authoritative coverage - skips
/// silently wherever Docker is not running, and the facts below hold on any relational provider: one
/// import commits whole or not at all, a replay re-reports its ledger row instead of importing twice,
/// a proposal is consumed exactly once, an Inventory that stopped being empty is refused with nothing
/// written, and a reference retired since the preview stops the import before any Stock exists.
///
/// What it deliberately does <em>not</em> prove is the serializable range lock that keeps the
/// empty-state assertion true until the entries commit. SQLite has a single writer, so it cannot
/// distinguish that guarantee from its absence; only <see cref="SqlImportExecutionStoreConcurrencyTests"/>
/// can, and it needs a real SQL Server to do it.
/// </summary>
public sealed class ImportExecutionStoreSqliteTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"u8.ToArray();

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());
    private readonly LocationId _location = new(Guid.NewGuid());

    public ImportExecutionStoreSqliteTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();

        db.Participants.Add(new ParticipantEntity
        {
            Id = _participant.Value,
            DisplayName = "Importing Owner",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = _participant.Value,
            ClientRequestId = Guid.NewGuid().ToString(),
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = _unit.Value,
            InventoryId = _inventory.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });
        db.Locations.Add(new LocationEntity
        {
            Id = _location.Value,
            InventoryId = _inventory.Value,
            Name = "Shelf A",
            NormalizedName = "shelf a",
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        db.SaveChanges();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task A_confirmed_import_creates_every_entry_with_its_audit_its_ledger_and_no_file_left_behind()
    {
        var proposal = await StorePendingAsync(
            Entry("Steel Bolts", 10.5m, note: "Blue box"),
            Entry("Brass Rivets", 0m, _location));

        using var db = CreateContext();
        var result = await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.Recorded!.CreatedEntryCount);
        Assert.Equal(proposal.FileDigest, result.Recorded.FileDigest);
        Assert.Equal(proposal.Id, result.Recorded.ProposalId);
        Assert.Equal(_participant, result.Recorded.ActorId);

        using var verify = CreateContext();
        var entries = await verify.StockEntries.AsNoTracking()
            .Where(e => e.InventoryId == _inventory.Value)
            .OrderBy(e => e.NormalizedName)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.Equal("Brass Rivets", entries[0].Name);
        Assert.Equal(0m, entries[0].Quantity);
        Assert.Equal(_location.Value, entries[0].LocationId);
        Assert.Equal("Steel Bolts", entries[1].Name);
        Assert.Equal(10.5m, entries[1].Quantity);
        Assert.Equal("Blue box", entries[1].Note);
        Assert.Null(entries[1].LocationId);

        // Exactly one fact, carrying nothing about what was imported.
        var audit = Assert.Single(await verify.InventoryAudits.AsNoTracking()
            .Where(a => a.InventoryId == _inventory.Value)
            .ToListAsync());
        Assert.Equal(nameof(AuditEventType.StockImported), audit.EventType);
        Assert.Equal(ImportFacts.CompletedOutcomeCode, audit.OutcomeCode);
        Assert.Equal(_participant.ToString(), audit.ActorId);
        Assert.Null(audit.SubjectParticipantId);

        var ledger = Assert.Single(await verify.ImportOperations.AsNoTracking()
            .Where(o => o.InventoryId == _inventory.Value)
            .ToListAsync());
        Assert.Equal(proposal.ExecutionOperationId.Value, ledger.OperationId);
        Assert.Equal(proposal.Id.Value, ledger.ProposalId);
        Assert.Equal(_participant.Value, ledger.ActorId);
        Assert.Equal(2, ledger.CreatedEntryCount);

        // The file goes with the import that used it, and the proposal is spent.
        Assert.Empty(await verify.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
        Assert.Equal(
            nameof(ImportProposalStatus.Confirmed),
            (await verify.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
    }

    [Fact]
    public async Task Applying_the_same_operation_identity_again_re_reports_it_instead_of_importing_twice()
    {
        var proposal = await StorePendingAsync();

        using var db = CreateContext();
        var first = await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);
        var replay = await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Applied, first.Outcome);
        Assert.Equal(ImportExecutionOutcome.AlreadyApplied, replay.Outcome);

        // Every semantic fact comes back off the ledger, the actor included - it is what tells a
        // second browser tab whether the import it is asking about was its own.
        Assert.Equal(first.Recorded!.OperationId, replay.Recorded!.OperationId);
        Assert.Equal(proposal.Id, replay.Recorded.ProposalId);
        Assert.Equal(_participant, replay.Recorded.ActorId);
        Assert.Equal(proposal.FileDigest, replay.Recorded.FileDigest);
        Assert.Equal(first.Recorded.CreatedEntryCount, replay.Recorded.CreatedEntryCount);

        using var verify = CreateContext();
        Assert.Single(await verify.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await verify.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await verify.ImportOperations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_recorded_import_is_found_only_from_the_Inventory_it_was_applied_to()
    {
        var proposal = await StorePendingAsync();

        using var db = CreateContext();
        var store = new SqlImportExecutionStore(db);
        await store.ApplyAsync(Command(proposal), CancellationToken.None);

        var recorded = await store.FindRecordedAsync(
            _inventory, proposal.ExecutionOperationId, CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Equal(_participant, recorded!.ActorId);
        Assert.Equal(proposal.Id, recorded.ProposalId);
        Assert.Equal(proposal.FileDigest, recorded.FileDigest);
        Assert.Equal(1, recorded.CreatedEntryCount);

        // A ledger row is only ever readable from the Inventory it belongs to, so a replayed token
        // can never report another Inventory's import as this one's.
        Assert.Null(await store.FindRecordedAsync(
            new InventoryId(Guid.NewGuid()), proposal.ExecutionOperationId, CancellationToken.None));
    }

    [Fact]
    public async Task An_import_into_an_Inventory_that_stopped_being_empty_changes_nothing()
    {
        var proposal = await StorePendingAsync();

        // A zero-quantity entry is still an entry, so this is exactly the case the gate exists for.
        using (var setup = CreateContext())
        {
            setup.StockEntries.Add(new StockEntryEntity
            {
                Id = Guid.NewGuid(),
                InventoryId = _inventory.Value,
                UnitId = _unit.Value,
                Name = "Existing",
                NormalizedName = "existing",
                Quantity = 0m,
                CreatedAt = Now,
            });
            await setup.SaveChangesAsync();
        }

        using var db = CreateContext();
        var result = await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);

        using var verify = CreateContext();
        var survivor = Assert.Single(await verify.StockEntries.AsNoTracking()
            .Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Equal("Existing", survivor.Name);
        Assert.Empty(await verify.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await verify.ImportOperations.AsNoTracking().ToListAsync());

        // Rolled back with everything else, so the caller settles it rather than the store burning it.
        Assert.Equal(
            nameof(ImportProposalStatus.Pending),
            (await verify.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
        Assert.Single(await verify.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
    }

    [Fact]
    public async Task An_import_naming_a_Unit_retired_since_the_preview_changes_nothing()
    {
        var proposal = await StorePendingAsync();

        using (var setup = CreateContext())
        {
            await setup.Units.Where(u => u.Id == _unit.Value)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.RetiredAt, Now));
        }

        using var db = CreateContext();
        var result = await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        await AssertNothingWasAppliedAsync(proposal);
    }

    [Fact]
    public async Task An_import_naming_a_Location_retired_since_the_preview_changes_nothing()
    {
        var proposal = await StorePendingAsync(Entry("Steel Bolts", 4m, _location));

        using (var setup = CreateContext())
        {
            await setup.Locations.Where(l => l.Id == _location.Value)
                .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.RetiredAt, Now));
        }

        using var db = CreateContext();
        var result = await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        await AssertNothingWasAppliedAsync(proposal);
    }

    /// <summary>Nothing at all happened: no Stock, no audit, no ledger, and the proposal and its file still there for the caller to settle.</summary>
    private async Task AssertNothingWasAppliedAsync(ImportProposal proposal)
    {
        using var verify = CreateContext();

        Assert.Empty(await verify.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await verify.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await verify.ImportOperations.AsNoTracking().ToListAsync());
        Assert.Equal(
            nameof(ImportProposalStatus.Pending),
            (await verify.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
        Assert.Single(await verify.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
    }

    [Fact]
    public async Task An_import_whose_proposal_was_already_settled_changes_nothing()
    {
        var proposal = await StorePendingAsync();

        using (var setup = CreateContext())
        {
            await new SqlImportProposalStore(setup).SettleAsync(
                proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);
        }

        using var db = CreateContext();
        var result = await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);

        using var verify = CreateContext();
        Assert.Empty(await verify.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await verify.ImportOperations.AsNoTracking().ToListAsync());
        Assert.Empty(await verify.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());

        // A settle is terminal: the refused import must not quietly relabel somebody else's decision.
        Assert.Equal(
            nameof(ImportProposalStatus.Rejected),
            (await verify.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
    }

    [Fact]
    public async Task Every_created_entry_is_a_real_Stock_Entry_the_conversation_can_then_read()
    {
        var proposal = await StorePendingAsync(Entry("  Steel Bolts  ", 4m, _location, "  Blue box  "));

        using var db = CreateContext();
        await new SqlImportExecutionStore(db).ApplyAsync(Command(proposal), CancellationToken.None);

        using var verify = CreateContext();
        var entry = await verify.StockEntries.AsNoTracking().SingleAsync(e => e.InventoryId == _inventory.Value);

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.NotEqual(Guid.Empty, entry.ConcurrencyStamp);

        // Through the domain factory, so an entry the import created is indistinguishable from one a
        // conversation created: trimmed display text, its normalized form, and its references.
        Assert.Equal("Steel Bolts", entry.Name);
        Assert.Equal("steel bolts", entry.NormalizedName);
        Assert.Equal("Blue box", entry.Note);
        Assert.Equal(_unit.Value, entry.UnitId);
        Assert.Equal(_location.Value, entry.LocationId);
        Assert.Equal(4m, entry.Quantity);
        Assert.Equal(Now, entry.CreatedAt);
    }

    private ImportEntry Entry(string name, decimal quantity, LocationId? locationId = null, string? note = null) => new()
    {
        LineNumber = 2,
        SourceLineNumbers = [2],
        Name = name,
        NormalizedName = NameNormalization.Normalize(name),
        Quantity = Quantity.Create(quantity),
        UnitId = _unit,
        UnitCanonicalName = "each",
        LocationId = locationId,
        LocationName = locationId is null ? null : "Shelf A",
        Note = note,
    };

    private ImportProposal Proposal(params ImportEntry[] entries) => ImportProposal.Create(
        ConfirmationToken.HashOf(ConfirmationToken.Issue()),
        _participant,
        _inventory,
        FileDigest.Of(RawContent),
        entries.Length == 0 ? [Entry("Steel Bolts", 4m)] : entries,
        EmptyStateVersion.Empty,
        Now);

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

    private async Task<ImportProposal> StorePendingAsync(params ImportEntry[] entries)
    {
        var proposal = Proposal(entries);

        using var db = CreateContext();
        await new SqlImportProposalStore(db).StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        return proposal;
    }

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);
}
