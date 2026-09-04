using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The one transaction Initial Import rests on, proved against real SQL Server under production
/// migrations: create every Stock Entry with its audit, its ledger, its proposal consumption and its
/// raw file's deletion - or change nothing at all. It also proves the claim the whole workflow
/// depends on: an import can never land in an Inventory that stopped being empty while it was being
/// reviewed.
///
/// The seeding shape is carried here rather than shared, exactly as every shipped SQL store test
/// class carries its own.
/// </summary>
public sealed class SqlImportExecutionStoreTests : SqlIntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"u8.ToArray();

    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());
    private readonly LocationId _location = new(Guid.NewGuid());

    [SkippableFact]
    public async Task A_confirmed_import_creates_every_entry_with_its_audit_its_ledger_and_no_file_left_behind()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(
            db,
            Entry("Steel Bolts", 10.5m, note: "Blue box"),
            Entry("Brass Rivets", 0m, _location));

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Applied, result.Outcome);
        Assert.Equal(2, result.Recorded!.CreatedEntryCount);
        Assert.Equal(proposal.FileDigest, result.Recorded.FileDigest);
        Assert.Equal(_participant, result.Recorded.ActorId);

        var entries = await db.StockEntries.AsNoTracking()
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
        var audit = Assert.Single(await db.InventoryAudits.AsNoTracking()
            .Where(a => a.InventoryId == _inventory.Value)
            .ToListAsync());
        Assert.Equal(nameof(AuditEventType.StockImported), audit.EventType);
        Assert.Equal(ImportFacts.CompletedOutcomeCode, audit.OutcomeCode);
        Assert.Equal(_participant.ToString(), audit.ActorId);
        Assert.Null(audit.SubjectParticipantId);

        var ledger = Assert.Single(await db.ImportOperations.AsNoTracking()
            .Where(o => o.InventoryId == _inventory.Value)
            .ToListAsync());
        Assert.Equal(proposal.ExecutionOperationId.Value, ledger.OperationId);
        Assert.Equal(proposal.Id.Value, ledger.ProposalId);
        Assert.Equal(_participant.Value, ledger.ActorId);
        Assert.Equal(2, ledger.CreatedEntryCount);

        Assert.Empty(await db.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
        Assert.Equal(
            nameof(ImportProposalStatus.Confirmed),
            (await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
    }

    [SkippableFact]
    public async Task Applying_the_same_operation_identity_again_re_reports_it_instead_of_importing_twice()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);

        var first = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);
        var replay = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Applied, first.Outcome);
        Assert.Equal(ImportExecutionOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(first.Recorded!.CreatedEntryCount, replay.Recorded!.CreatedEntryCount);

        // The actor comes back off the ledger, because that is what tells a second browser tab whether
        // the import it is asking about was its own.
        Assert.Equal(_participant, replay.Recorded.ActorId);
        Assert.Equal(proposal.Id, replay.Recorded.ProposalId);
        Assert.Equal(proposal.FileDigest, replay.Recorded.FileDigest);

        Assert.Single(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Single(await db.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task A_recorded_import_is_found_only_from_the_Inventory_it_was_applied_to()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);
        var store = Store(db);
        await store.ApplyAsync(Command(proposal), CancellationToken.None);

        var recorded = await store.FindRecordedAsync(_inventory, proposal.ExecutionOperationId, CancellationToken.None);

        Assert.NotNull(recorded);
        Assert.Equal(_participant, recorded!.ActorId);
        Assert.Equal(proposal.Id, recorded.ProposalId);
        Assert.Equal(proposal.FileDigest, recorded.FileDigest);
        Assert.Equal(1, recorded.CreatedEntryCount);

        Assert.Null(await store.FindRecordedAsync(
            new InventoryId(Guid.NewGuid()), proposal.ExecutionOperationId, CancellationToken.None));
    }

    [SkippableFact]
    public async Task An_import_into_an_Inventory_that_stopped_being_empty_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);

        // A zero-quantity entry is still an entry, so this is exactly the case the gate exists for.
        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventory.Value,
            UnitId = _unit.Value,
            Name = "Existing",
            NormalizedName = "existing",
            Quantity = 0m,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);
        Assert.Single(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.InventoryAudits.AsNoTracking().Where(a => a.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.ImportOperations.AsNoTracking().ToListAsync());

        // Rolled back with everything else, so the caller settles it rather than the store burning it.
        Assert.Equal(
            nameof(ImportProposalStatus.Pending),
            (await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
        Assert.Single(await db.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());
    }

    [SkippableFact]
    public async Task An_import_whose_proposal_was_already_settled_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);
        await new SqlImportProposalStore(db).SettleAsync(
            proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Empty(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.ImportOperations.AsNoTracking().ToListAsync());

        // A settle is terminal: the refused import must not quietly relabel somebody else's decision.
        Assert.Equal(
            nameof(ImportProposalStatus.Rejected),
            (await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
    }

    [SkippableFact]
    public async Task An_import_naming_a_Unit_retired_since_the_preview_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db);

        await db.Units.Where(u => u.Id == _unit.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.RetiredAt, Now));
        db.ChangeTracker.Clear();

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Empty(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.ImportOperations.AsNoTracking().ToListAsync());
        Assert.Equal(
            nameof(ImportProposalStatus.Pending),
            (await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposal.Id.Value)).Status);
    }

    [SkippableFact]
    public async Task An_import_naming_a_Location_retired_since_the_preview_changes_nothing()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db, Entry("Steel Bolts", 4m, _location));

        await db.Locations.Where(l => l.Id == _location.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(l => l.RetiredAt, Now));
        db.ChangeTracker.Clear();

        var result = await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Empty(await db.StockEntries.AsNoTracking().Where(e => e.InventoryId == _inventory.Value).ToListAsync());
        Assert.Empty(await db.ImportOperations.AsNoTracking().ToListAsync());
    }

    [SkippableFact]
    public async Task Every_created_entry_is_a_real_Stock_Entry_the_conversation_can_then_read()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import write.");

        await SeedAsync();
        using var db = NewContext();
        var proposal = await StorePendingAsync(db, Entry("Steel Bolts", 4m, _location, "Blue box"));

        await Store(db).ApplyAsync(Command(proposal), CancellationToken.None);

        var entry = await db.StockEntries.AsNoTracking().SingleAsync(e => e.InventoryId == _inventory.Value);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.NotEqual(Guid.Empty, entry.ConcurrencyStamp);
        Assert.Equal("steel bolts", entry.NormalizedName);
        Assert.Equal(_unit.Value, entry.UnitId);
        Assert.Equal(_location.Value, entry.LocationId);
        Assert.Equal("Blue box", entry.Note);
        Assert.Equal(4m, entry.Quantity);
    }

    private MultiChannelAgentDbContext NewContext() =>
        Factory!.Services.CreateScope().ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private static SqlImportExecutionStore Store(MultiChannelAgentDbContext db) => new(db);

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

    private async Task<ImportProposal> StorePendingAsync(MultiChannelAgentDbContext db, params ImportEntry[] entries)
    {
        var proposal = Proposal(entries);
        await new SqlImportProposalStore(db).StoreAsync(proposal, RawContent, Now, CancellationToken.None);
        db.ChangeTracker.Clear();

        return proposal;
    }

    private async Task SeedAsync()
    {
        using var db = NewContext();

        db.Participants.Add(new ParticipantEntity
        {
            Id = _participant.Value,
            DisplayName = "Owner Person",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = _inventory.Value,
            Name = $"Warehouse {_inventory.Value:N}",
            NormalizedName = $"warehouse {_inventory.Value:N}",
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

        await db.SaveChangesAsync();
    }
}
