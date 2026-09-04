using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

/// <summary>
/// Pins the contract the double and its SQL twin must both satisfy: replay by operation identity,
/// single-use proposal consumption, the authoritative empty-state re-check, and nothing at all
/// written - including the proposal staying Pending - when either refuses.
/// </summary>
public sealed class InMemoryImportExecutionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly InventoryId Inventory = new(Guid.NewGuid());
    private static readonly ParticipantId Actor = new(Guid.NewGuid());
    private static readonly FileDigest Digest = FileDigest.Of(new byte[] { 1, 2, 3 });

    private static readonly IReadOnlyList<ImportEntry> Entries =
    [
        new ImportEntry
        {
            LineNumber = 2,
            SourceLineNumbers = [2],
            Name = "Steel Bolts",
            NormalizedName = "steel bolts",
            Quantity = Quantity.Create(10m),
            UnitId = new UnitId(Guid.NewGuid()),
            UnitCanonicalName = "each",
        },
    ];

    private static ImportExecutionCommand Command(ImportProposalId proposalId, ImportOperationId? operationId = null) =>
        new()
        {
            OperationId = operationId ?? ImportOperationId.DeriveForProposal(proposalId),
            InventoryId = Inventory,
            ActorId = Actor,
            ConsumesProposalId = proposalId,
            FileDigest = Digest,
            Entries = Entries,
            EmptyStateVersion = EmptyStateVersion.Empty,
            Now = Now,
        };

    private static async Task<ImportProposalId> StorePendingProposalAsync(InMemoryImportProposalStore proposalStore)
    {
        var proposal = ImportProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Actor,
            Inventory,
            Digest,
            Entries,
            EmptyStateVersion.Empty,
            Now);

        await proposalStore.StoreAsync(proposal, new byte[] { 1, 2, 3 }, Now, CancellationToken.None);

        return proposal.Id;
    }

    [Fact]
    public async Task Applying_a_command_creates_the_entries_records_one_audit_and_returns_the_recorded_import()
    {
        var proposalStore = new InMemoryImportProposalStore();
        var emptyState = new InMemoryStockEmptyStateReader();
        var store = new InMemoryImportExecutionStore(proposalStore, emptyState);
        var proposalId = await StorePendingProposalAsync(proposalStore);

        var result = await store.ApplyAsync(Command(proposalId), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Applied, result.Outcome);
        Assert.Equal(1, result.Recorded!.CreatedEntryCount);
        Assert.Equal(Entries, store.CreatedEntries);
        Assert.Single(store.Audits);
        Assert.Equal(AuditEventType.StockImported, store.Audits[0].EventType);
        Assert.Equal(ImportProposalStatus.Confirmed, await proposalStore.FindStatusAsync(proposalId, CancellationToken.None));
        Assert.True(await emptyState.AnyStockAsync(Inventory, CancellationToken.None));
    }

    [Fact]
    public async Task Replaying_the_same_operation_returns_AlreadyApplied_and_creates_nothing_more()
    {
        var proposalStore = new InMemoryImportProposalStore();
        var emptyState = new InMemoryStockEmptyStateReader();
        var store = new InMemoryImportExecutionStore(proposalStore, emptyState);
        var proposalId = await StorePendingProposalAsync(proposalStore);
        var command = Command(proposalId);
        var first = await store.ApplyAsync(command, CancellationToken.None);

        var replay = await store.ApplyAsync(command, CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.AlreadyApplied, replay.Outcome);
        Assert.Equal(first.Recorded, replay.Recorded);
        Assert.Equal(Entries.Count, store.CreatedEntries.Count);
        Assert.Single(store.Audits);
    }

    [Fact]
    public async Task A_conflicting_empty_state_leaves_the_proposal_pending_and_writes_nothing()
    {
        var proposalStore = new InMemoryImportProposalStore();
        var emptyState = new InMemoryStockEmptyStateReader();
        var store = new InMemoryImportExecutionStore(proposalStore, emptyState);
        var proposalId = await StorePendingProposalAsync(proposalStore);
        emptyState.SetAnyStock(Inventory, true);

        var result = await store.ApplyAsync(Command(proposalId), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);
        Assert.Empty(store.CreatedEntries);
        Assert.Empty(store.Audits);
        Assert.Equal(ImportProposalStatus.Pending, await proposalStore.FindStatusAsync(proposalId, CancellationToken.None));
        Assert.NotNull(await proposalStore.FindRawContentAsync(proposalId, CancellationToken.None));
    }

    [Fact]
    public async Task A_proposal_that_is_no_longer_pending_conflicts_and_writes_nothing()
    {
        var proposalStore = new InMemoryImportProposalStore();
        var emptyState = new InMemoryStockEmptyStateReader();
        var store = new InMemoryImportExecutionStore(proposalStore, emptyState);
        var proposalId = await StorePendingProposalAsync(proposalStore);
        await proposalStore.SettleAsync(proposalId, ImportProposalStatus.Rejected, Now, CancellationToken.None);

        var result = await store.ApplyAsync(Command(proposalId), CancellationToken.None);

        Assert.Equal(ImportExecutionOutcome.Conflict, result.Outcome);
        Assert.Null(result.Recorded);
        Assert.Empty(store.CreatedEntries);
        Assert.Empty(store.Audits);
        Assert.Equal(ImportProposalStatus.Rejected, await proposalStore.FindStatusAsync(proposalId, CancellationToken.None));
    }
}
