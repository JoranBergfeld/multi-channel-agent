using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Docker-free coverage of the import store's whole lifecycle against a real relational engine, in
/// the shape <see cref="SqlInboxStoreConcurrencyTests"/> established: one shared-cache in-memory
/// SQLite database, and a fresh <see cref="MultiChannelAgentDbContext"/> per store, exactly as each
/// request scope resolves its own in production.
///
/// It exists because <see cref="SqlImportProposalStoreTests"/> - the authoritative SQL Server
/// coverage - silently skips wherever Docker is not running, and the facts proved here are ones no
/// double can prove: that the exact entries survive serialization, that a stored proposal this
/// process cannot read is refused rather than guessed at, that the raw bytes belong to the store
/// rather than to whoever handed them over, that both sweeps stay inside their bound and their
/// cutoff, and that a store that fails or is cancelled leaves neither a row nor a staged entity
/// behind for an unrelated Turn to commit.
/// </summary>
public sealed class ImportProposalStoreSqliteTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,10.5,,Shelf A,Blue box\n"u8.ToArray();

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly InventoryId _otherInventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());
    private readonly LocationId _location = new(Guid.NewGuid());
    private readonly ParticipantId _participant;

    public ImportProposalStoreSqliteTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();

        _participant = NewParticipant();

        foreach (var inventoryId in (Guid[])[_inventory.Value, _otherInventory.Value])
        {
            db.Inventories.Add(new InventoryEntity
            {
                Id = inventoryId,
                Name = $"Warehouse {inventoryId:N}",
                NormalizedName = $"warehouse {inventoryId:N}",
                CreatedByParticipantId = _participant.Value,
                ClientRequestId = Guid.NewGuid().ToString(),
                CreatedAt = Now,
            });
        }

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
    public async Task Every_exact_entry_a_stored_import_carries_survives_the_round_trip()
    {
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);
        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            _participant,
            _inventory,
            FileDigest.Of(RawContent),
            [
                new ImportEntry
                {
                    LineNumber = 2,
                    SourceLineNumbers = [2, 5, 9],
                    Name = "Steel Bolts",
                    NormalizedName = NameNormalization.Normalize("Steel Bolts"),
                    // Ten decimal places: the exact amount the domain permits, and precisely what a
                    // JSON number could quietly round on the way back.
                    Quantity = Quantity.Create(10.0000000001m),
                    UnitId = _unit,
                    UnitCanonicalName = "each",
                    LocationId = _location,
                    LocationName = "Shelf A",
                    Note = "Blue box",
                },
                new ImportEntry
                {
                    LineNumber = 3,
                    SourceLineNumbers = [3],
                    Name = "Brass Rivets",
                    NormalizedName = NameNormalization.Normalize("Brass Rivets"),
                    Quantity = Quantity.Zero,
                    UnitId = _unit,
                    UnitCanonicalName = "each",
                    LocationId = null,
                    LocationName = null,
                    Note = null,
                },
            ],
            EmptyStateVersion.Empty,
            Now);

        Assert.False(await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None));

        using var reader = CreateContext();
        var read = await new SqlImportProposalStore(reader)
            .FindPendingAsync(_participant, _inventory, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(proposal.Id, read!.Id);
        Assert.Equal(proposal.TokenHash, read.TokenHash);
        Assert.Equal(proposal.FileDigest, read.FileDigest);
        Assert.Equal(EmptyStateVersion.Empty, read.EmptyStateVersion);
        Assert.Equal(proposal.CreatedAt, read.CreatedAt);
        Assert.Equal(proposal.ExpiresAt, read.ExpiresAt);
        Assert.Equal(proposal.ExecutionOperationId, read.ExecutionOperationId);

        Assert.Collection(
            read.Entries,
            first =>
            {
                Assert.Equal(2, first.LineNumber);
                Assert.Equal([2, 5, 9], first.SourceLineNumbers);
                Assert.Equal("Steel Bolts", first.Name);
                Assert.Equal("steel bolts", first.NormalizedName);
                Assert.Equal("10.0000000001", first.Quantity.ToInvariantText());
                Assert.Equal(_unit, first.UnitId);
                Assert.Equal("each", first.UnitCanonicalName);
                Assert.Equal(_location, first.LocationId);
                Assert.Equal("Shelf A", first.LocationName);
                Assert.Equal("Blue box", first.Note);
            },
            second =>
            {
                Assert.Equal(3, second.LineNumber);
                Assert.Equal("Brass Rivets", second.Name);
                Assert.Equal("0", second.Quantity.ToInvariantText());
                Assert.Null(second.LocationId);
                Assert.Null(second.LocationName);
                Assert.Null(second.Note);
            });
    }

    [Fact]
    public async Task A_pending_import_is_visible_only_to_the_Participant_and_Inventory_it_was_bound_to()
    {
        var stranger = NewParticipant();
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);
        await store.StoreAsync(Proposal(), RawContent, Now, CancellationToken.None);

        Assert.NotNull(await store.FindPendingAsync(_participant, _inventory, CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(stranger, _inventory, CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(_participant, _otherInventory, CancellationToken.None));
    }

    [Fact]
    public async Task A_second_pending_import_for_one_Participant_and_Inventory_cannot_exist_at_all()
    {
        using (var writer = CreateContext())
        {
            await new SqlImportProposalStore(writer).StoreAsync(Proposal(), RawContent, Now, CancellationToken.None);
        }

        // Deliberately bypasses the store: the invariant must be the database's, not the code's.
        using var smuggler = CreateContext();
        smuggler.ImportProposals.Add(new ImportProposalEntity
        {
            ProposalId = Guid.NewGuid(),
            TokenHash = ConfirmationToken.HashOf(ConfirmationToken.Issue()).Value,
            ParticipantId = _participant.Value,
            InventoryId = _inventory.Value,
            FileDigest = FileDigest.Of(RawContent).Value,
            Status = nameof(ImportProposalStatus.Pending),
            EntriesJson = "{}",
            ExpectedStockEntryCount = 0,
            CreatedAt = Now,
            ExpiresAt = Now.AddMinutes(ImportProposal.LifetimeMinutes),
            ExpiresAtTicks = Now.AddMinutes(ImportProposal.LifetimeMinutes).UtcTicks,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => smuggler.SaveChangesAsync());
    }

    [Fact]
    public async Task Storing_a_second_import_supersedes_the_first_and_discards_its_file_in_the_same_transaction()
    {
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);
        var first = Proposal();
        await store.StoreAsync(first, RawContent, Now, CancellationToken.None);

        var second = Proposal();
        Assert.True(await store.StoreAsync(second, RawContent, Now.AddMinutes(1), CancellationToken.None));

        Assert.Equal(ImportProposalStatus.Superseded, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(first.Id, CancellationToken.None));

        var pending = await store.FindPendingAsync(_participant, _inventory, CancellationToken.None);
        Assert.Equal(second.Id, pending!.Id);
        Assert.NotNull(await store.FindRawContentAsync(second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Only_one_caller_can_win_a_settle_and_the_file_goes_with_the_winner()
    {
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal();
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        Assert.True(await store.SettleAsync(proposal.Id, ImportProposalStatus.Confirmed, Now, CancellationToken.None));
        Assert.False(await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None));

        Assert.Equal(ImportProposalStatus.Confirmed, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(_participant, _inventory, CancellationToken.None));

        // A settle that found nothing pending must not be reported as a win either.
        Assert.False(await store.SettleAsync(
            ImportProposalId.NewId(), ImportProposalStatus.Rejected, Now, CancellationToken.None));
    }

    [Fact]
    public async Task The_expiry_sweep_stays_inside_its_bound_and_touches_only_imports_past_their_lifetime()
    {
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);

        var earliest = await StoreForNewParticipantAsync(store, Now);
        var middle = await StoreForNewParticipantAsync(store, Now.AddMinutes(1));
        var latest = await StoreForNewParticipantAsync(store, Now.AddMinutes(5));

        var cutoff = Now.AddMinutes(ImportProposal.LifetimeMinutes + 1);

        Assert.Equal(1, await store.ExpirePendingBeforeAsync(cutoff, maxRows: 1, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Expired, await store.FindStatusAsync(earliest.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(earliest.Id, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Pending, await store.FindStatusAsync(middle.Id, CancellationToken.None));

        Assert.Equal(1, await store.ExpirePendingBeforeAsync(cutoff, maxRows: 100, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Expired, await store.FindStatusAsync(middle.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(middle.Id, CancellationToken.None));

        // The one whose ten minutes have not run out is untouched, file included.
        Assert.Equal(ImportProposalStatus.Pending, await store.FindStatusAsync(latest.Id, CancellationToken.None));
        Assert.NotNull(await store.FindRawContentAsync(latest.Id, CancellationToken.None));

        Assert.Equal(0, await store.ExpirePendingBeforeAsync(cutoff, maxRows: 100, CancellationToken.None));
    }

    [Fact]
    public async Task The_retention_sweep_deletes_only_settled_imports_past_the_cutoff_and_within_its_bound()
    {
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);

        var first = await StoreForNewParticipantAsync(store, Now);
        var second = await StoreForNewParticipantAsync(store, Now);
        var late = await StoreForNewParticipantAsync(store, Now);
        var pending = await StoreForNewParticipantAsync(store, Now);

        await store.SettleAsync(first.Id, ImportProposalStatus.Confirmed, Now, CancellationToken.None);
        await store.SettleAsync(second.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);
        await store.SettleAsync(late.Id, ImportProposalStatus.Rejected, Now.AddHours(2), CancellationToken.None);

        var cutoff = Now.AddHours(1);

        Assert.Equal(1, await store.DeleteSettledBeforeAsync(cutoff, maxRows: 1, CancellationToken.None));
        Assert.Equal(1, await store.DeleteSettledBeforeAsync(cutoff, maxRows: 100, CancellationToken.None));
        Assert.Equal(0, await store.DeleteSettledBeforeAsync(cutoff, maxRows: 100, CancellationToken.None));

        Assert.Null(await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Null(await store.FindStatusAsync(second.Id, CancellationToken.None));

        // Settled after the cutoff, and never settled at all: neither is retention's business.
        Assert.Equal(ImportProposalStatus.Rejected, await store.FindStatusAsync(late.Id, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Pending, await store.FindStatusAsync(pending.Id, CancellationToken.None));
        Assert.NotNull(await store.FindRawContentAsync(pending.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_stored_bytes_are_exactly_the_ones_offered_and_no_more()
    {
        // A slice of a larger buffer, as a parser or a pooled read buffer hands one over: what is
        // stored must be the offered window, not whatever array happens to be underneath it.
        var padded = new byte[RawContent.Length + 6];
        Array.Fill(padded, (byte)0xAA);
        RawContent.CopyTo(padded, 3);
        var offered = padded.AsMemory(3, RawContent.Length);

        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal();

        await store.StoreAsync(proposal, offered, Now, CancellationToken.None);

        // The caller reuses its buffer, as a pooled or reused upload buffer would.
        Array.Clear(padded);

        var stored = await store.FindRawContentAsync(proposal.Id, CancellationToken.None);
        Assert.Equal(RawContent, stored!.Value.ToArray());
    }

    [Fact]
    public async Task A_stored_import_this_process_cannot_read_is_refused_rather_than_guessed_at()
    {
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal();
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        var original = await db.ImportProposals
            .AsNoTracking()
            .Where(p => p.ProposalId == proposal.Id.Value)
            .Select(p => p.EntriesJson)
            .SingleAsync();

        // The version is written so a later shape change is detected rather than silently mis-read.
        Assert.Contains("\"Version\":1", original, StringComparison.Ordinal);
        await SetEntriesJsonAsync(db, proposal, original.Replace("\"Version\":1", "\"Version\":99", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.FindPendingAsync(_participant, _inventory, CancellationToken.None));

        // An amount that is not an amount is refused; it is never rounded, defaulted, or skipped.
        Assert.Contains("\"10.5\"", original, StringComparison.Ordinal);
        await SetEntriesJsonAsync(db, proposal, original.Replace("\"10.5\"", "\"ten and a half\"", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.FindPendingAsync(_participant, _inventory, CancellationToken.None));

        await SetEntriesJsonAsync(db, proposal, "null");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.FindPendingAsync(_participant, _inventory, CancellationToken.None));

        await SetEntriesJsonAsync(db, proposal, original);
        await db.ImportProposals
            .Where(p => p.ProposalId == proposal.Id.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.FileDigest, new string('z', 64)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.FindPendingAsync(_participant, _inventory, CancellationToken.None));
    }

    [Fact]
    public async Task A_store_that_fails_leaves_neither_a_staged_row_nor_a_superseded_one_behind()
    {
        var proposal = Proposal();
        using (var writer = CreateContext())
        {
            await new SqlImportProposalStore(writer).StoreAsync(proposal, RawContent, Now, CancellationToken.None);
        }

        using var loser = CreateContext();
        var store = new SqlImportProposalStore(loser);

        // The very same proposal again, from a second scope: superseding succeeds, the insert then
        // loses on the primary key, and the whole attempt must come undone.
        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => store.StoreAsync(proposal, RawContent, Now.AddMinutes(1), CancellationToken.None));

        // The DbContext serves a whole batch of Turns: an entity left Added here would be committed
        // later by a Turn that never asked for it.
        Assert.Empty(loser.ChangeTracker.Entries());

        using var verifier = CreateContext();
        var verifying = new SqlImportProposalStore(verifier);
        Assert.Equal(ImportProposalStatus.Pending, await verifying.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.NotNull(await verifying.FindPendingAsync(_participant, _inventory, CancellationToken.None));
        Assert.NotNull(await verifying.FindRawContentAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_cancelled_store_writes_nothing_and_leaves_the_context_clean()
    {
        using var db = CreateContext();
        var store = new SqlImportProposalStore(db);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.StoreAsync(Proposal(), RawContent, Now, cancelled.Token));

        Assert.Empty(db.ChangeTracker.Entries());

        using var verifier = CreateContext();
        Assert.Null(await new SqlImportProposalStore(verifier)
            .FindPendingAsync(_participant, _inventory, CancellationToken.None));
        Assert.Empty(await verifier.ImportUploads.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task An_Inventory_holding_nothing_but_a_zero_quantity_entry_is_not_empty()
    {
        using var db = CreateContext();
        var reader = new SqlStockEmptyStateReader(db);

        Assert.False(await reader.AnyStockAsync(_inventory, CancellationToken.None));

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = _inventory.Value,
            UnitId = _unit.Value,
            Name = "Steel Bolts",
            NormalizedName = "steel bolts",
            Quantity = 0m,
            CreatedAt = Now,
        });
        await db.SaveChangesAsync();

        Assert.True(await reader.AnyStockAsync(_inventory, CancellationToken.None));

        // Emptiness is asked about one Inventory, never about the database.
        Assert.False(await reader.AnyStockAsync(_otherInventory, CancellationToken.None));
    }

    private async Task<ImportProposal> StoreForNewParticipantAsync(SqlImportProposalStore store, DateTimeOffset createdAt)
    {
        var proposal = Proposal(NewParticipant(), createdAt);
        await store.StoreAsync(proposal, RawContent, createdAt, CancellationToken.None);
        return proposal;
    }

    private ImportProposal Proposal(ParticipantId? participantId = null, DateTimeOffset? createdAt = null) =>
        ImportProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            participantId ?? _participant,
            _inventory,
            FileDigest.Of(RawContent),
            [
                new ImportEntry
                {
                    LineNumber = 2,
                    SourceLineNumbers = [2],
                    Name = "Steel Bolts",
                    NormalizedName = "steel bolts",
                    Quantity = Quantity.Create(10.5m),
                    UnitId = _unit,
                    UnitCanonicalName = "each",
                    LocationId = _location,
                    LocationName = "Shelf A",
                    Note = "Blue box",
                },
            ],
            EmptyStateVersion.Empty,
            createdAt ?? Now);

    private static Task SetEntriesJsonAsync(MultiChannelAgentDbContext db, ImportProposal proposal, string json) =>
        db.ImportProposals
            .Where(p => p.ProposalId == proposal.Id.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.EntriesJson, json));

    private ParticipantId NewParticipant()
    {
        var participantId = new ParticipantId(Guid.NewGuid());

        using var db = CreateContext();
        db.Participants.Add(new ParticipantEntity
        {
            Id = participantId.Value,
            DisplayName = "Importing Owner",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.SaveChanges();

        return participantId;
    }

    private MultiChannelAgentDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString).Options);
}
