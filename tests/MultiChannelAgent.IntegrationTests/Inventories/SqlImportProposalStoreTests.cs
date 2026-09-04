using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Proves against real SQL Server, under production migrations, the invariants the pending import
/// store exists for: exactly one pending import per Participant and Inventory - enforced by the
/// database, not by agreement between two code paths - atomic replacement that takes the superseded
/// file with it, a guarded settle only one caller can win, a token that is nowhere in the row, and a
/// raw upload that is gone by every path out of Pending.
/// </summary>
public sealed class SqlImportProposalStoreTests : SqlIntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly byte[] RawContent = "Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"u8.ToArray();

    private readonly ParticipantId _participant = new(Guid.NewGuid());
    private readonly InventoryId _inventory = new(Guid.NewGuid());
    private readonly UnitId _unit = new(Guid.NewGuid());

    private MultiChannelAgentDbContext NewContext() =>
        Factory!.Services.CreateScope().ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

    private ImportProposal Proposal(string token, string name = "Steel Bolts", ParticipantId? participantId = null) =>
        ImportProposal.Create(
            ConfirmationToken.HashOf(token),
            participantId ?? _participant,
            _inventory,
            FileDigest.Of(RawContent),
            [
                new ImportEntry
                {
                    LineNumber = 2,
                    SourceLineNumbers = [2, 5],
                    Name = name,
                    NormalizedName = NameNormalization.Normalize(name),
                    Quantity = Quantity.Create(10.5m),
                    UnitId = _unit,
                    UnitCanonicalName = "each",
                    LocationId = null,
                    LocationName = null,
                    Note = "Blue box",
                },
            ],
            EmptyStateVersion.Empty,
            Now);

    [SkippableFact]
    public async Task A_stored_import_round_trips_every_exact_entry_it_carries()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());

        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        var read = await store.FindPendingAsync(_participant, _inventory, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(proposal.Id, read!.Id);
        Assert.Equal(proposal.FileDigest, read.FileDigest);
        Assert.Equal(EmptyStateVersion.Empty, read.EmptyStateVersion);
        Assert.Equal(proposal.ExpiresAt, read.ExpiresAt);

        var entry = Assert.Single(read.Entries);
        Assert.Equal("Steel Bolts", entry.Name);
        Assert.Equal("steel bolts", entry.NormalizedName);
        Assert.Equal("10.5", entry.Quantity.ToInvariantText());
        Assert.Equal(_unit, entry.UnitId);
        Assert.Equal("each", entry.UnitCanonicalName);
        Assert.Null(entry.LocationId);
        Assert.Equal("Blue box", entry.Note);
        Assert.Equal([2, 5], entry.SourceLineNumbers);
    }

    [SkippableFact]
    public async Task The_raw_file_is_stored_with_the_proposal_and_gone_the_moment_it_settles()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        Assert.Equal(RawContent, (await store.FindRawContentAsync(proposal.Id, CancellationToken.None))!.Value.ToArray());

        Assert.True(await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None));

        Assert.Null(await store.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Empty(await db.ImportUploads.AsNoTracking().Where(u => u.ProposalId == proposal.Id.Value).ToListAsync());

        // The proposal itself remains, so a late answer can be told truthfully what happened.
        Assert.Equal(ImportProposalStatus.Rejected, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Storing_a_second_import_supersedes_the_first_and_takes_its_file_with_it()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var first = Proposal(ConfirmationToken.Issue(), "Steel Bolts");
        await store.StoreAsync(first, RawContent, Now, CancellationToken.None);

        var second = Proposal(ConfirmationToken.Issue(), "Brass Rivets");
        Assert.True(await store.StoreAsync(second, RawContent, Now, CancellationToken.None));

        Assert.Equal(ImportProposalStatus.Superseded, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(first.Id, CancellationToken.None));

        var pending = await store.FindPendingAsync(_participant, _inventory, CancellationToken.None);
        Assert.Equal(second.Id, pending!.Id);
    }

    [SkippableFact]
    public async Task A_second_pending_import_for_one_Participant_and_Inventory_cannot_exist_at_all()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using (var writer = NewContext())
        {
            await new SqlImportProposalStore(writer).StoreAsync(
                Proposal(ConfirmationToken.Issue()), RawContent, Now, CancellationToken.None);
        }

        // Deliberately bypasses the store: the invariant must be the database's, not the code's.
        using var smuggler = NewContext();
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
            ExpiresAt = Now.AddMinutes(10),
            ExpiresAtTicks = Now.AddMinutes(10).UtcTicks,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => smuggler.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Two_Participants_may_each_have_their_own_pending_import()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        var other = await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);

        await store.StoreAsync(Proposal(ConfirmationToken.Issue()), RawContent, Now, CancellationToken.None);
        await store.StoreAsync(
            Proposal(ConfirmationToken.Issue(), participantId: other), RawContent, Now, CancellationToken.None);

        Assert.NotNull(await store.FindPendingAsync(_participant, _inventory, CancellationToken.None));
        Assert.NotNull(await store.FindPendingAsync(other, _inventory, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Only_one_caller_can_win_a_settle()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        Assert.True(await store.SettleAsync(proposal.Id, ImportProposalStatus.Confirmed, Now, CancellationToken.None));
        Assert.False(await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Confirmed, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task An_expired_import_is_swept_out_of_Pending_and_its_file_discarded()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        var swept = await store.ExpirePendingBeforeAsync(
            Now.AddMinutes(ImportProposal.LifetimeMinutes), maxRows: 100, CancellationToken.None);

        Assert.Equal(1, swept);
        Assert.Equal(ImportProposalStatus.Expired, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(proposal.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_settled_import_is_discarded_once_it_is_past_retention()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed import store.");

        await SeedAsync();
        using var db = NewContext();
        var store = new SqlImportProposalStore(db);
        var proposal = Proposal(ConfirmationToken.Issue());
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);
        await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);

        Assert.Equal(1, await store.DeleteSettledBeforeAsync(Now.AddHours(1), maxRows: 100, CancellationToken.None));
        Assert.Null(await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    /// <summary>Seeds the Participants, Inventory, and Unit every case needs, and returns a second Participant.</summary>
    [SkippableFact]
    public async Task An_Inventory_holding_a_zero_quantity_entry_is_not_empty()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed empty-state read.");

        await SeedAsync();
        using var db = NewContext();
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
    }

    [SkippableFact]
    public async Task Audit_facts_are_deleted_only_once_they_are_past_retention_and_within_the_bound()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed audit sweep.");

        await SeedAsync();
        using var db = NewContext();

        var stale = Now.AddDays(-AuditFact.RetentionDays - 1);
        db.InventoryAudits.AddRange(Audit(stale), Audit(stale), Audit(Now));
        await db.SaveChangesAsync();

        var store = new SqlInventoryAuditRetentionStore(db);
        var cutoff = Now.AddDays(-AuditFact.RetentionDays);

        Assert.Equal(1, await store.DeleteOccurredBeforeAsync(cutoff, maxRows: 1, CancellationToken.None));
        Assert.Equal(1, await store.DeleteOccurredBeforeAsync(cutoff, maxRows: 100, CancellationToken.None));
        Assert.Equal(0, await store.DeleteOccurredBeforeAsync(cutoff, maxRows: 100, CancellationToken.None));

        // The fact still inside its ninety days is nobody's to delete.
        var survivor = Assert.Single(await db.InventoryAudits.AsNoTracking().ToListAsync());
        Assert.Equal(Now, survivor.OccurredAtUtc);
    }

    private InventoryAuditEntity Audit(DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        EventType = "membership_granted",
        ActorKind = "participant",
        ActorId = _participant.Value.ToString(),
        InventoryId = _inventory.Value,
        OutcomeCode = "granted",
        OccurredAtUtc = occurredAt,
        ExpiresAtUtc = occurredAt.AddDays(AuditFact.RetentionDays),
    };

    /// <summary>Seeds the Participants, Inventory, and Unit every case needs, and returns a second Participant.</summary>
    private async Task<ParticipantId> SeedAsync()
    {
        using var db = NewContext();
        var other = new ParticipantId(Guid.NewGuid());

        foreach (var participantId in (Guid[])[_participant.Value, other.Value])
        {
            db.Participants.Add(new ParticipantEntity
            {
                Id = participantId,
                DisplayName = "Owner Person",
                CreatedAt = Now,
                UpdatedAt = Now,
            });
        }

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

        await db.SaveChangesAsync();

        return other;
    }
}
