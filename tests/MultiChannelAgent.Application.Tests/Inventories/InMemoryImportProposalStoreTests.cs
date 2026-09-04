using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

/// <summary>
/// Pins the contract the double and its SQL twin must both satisfy: one pending proposal per
/// Participant and Inventory, a guarded settle only one caller can win, and a raw upload that is
/// discarded by every path out of Pending.
/// </summary>
public sealed class InMemoryImportProposalStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.NewGuid());
    private static readonly InventoryId Inventory = new(Guid.NewGuid());
    private static readonly ReadOnlyMemory<byte> RawContent = new byte[] { 1, 2, 3 };

    private static ImportProposal Proposal(ParticipantId? participantId = null, InventoryId? inventoryId = null) =>
        ImportProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            participantId ?? Participant,
            inventoryId ?? Inventory,
            FileDigest.Of(RawContent.Span),
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
            ],
            EmptyStateVersion.Empty,
            Now);

    [Fact]
    public async Task A_stored_proposal_is_found_by_its_Participant_and_Inventory_with_its_raw_content()
    {
        var store = new InMemoryImportProposalStore();
        var proposal = Proposal();

        var superseded = await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        Assert.False(superseded);
        Assert.Equal(proposal.Id, (await store.FindPendingAsync(Participant, Inventory, CancellationToken.None))!.Id);
        Assert.Equal(RawContent.ToArray(), (await store.FindRawContentAsync(proposal.Id, CancellationToken.None))!.Value.ToArray());
    }

    [Fact]
    public async Task Storing_a_new_proposal_supersedes_the_pending_one_and_discards_its_raw_content()
    {
        var store = new InMemoryImportProposalStore();
        var first = Proposal();
        var second = Proposal();
        await store.StoreAsync(first, RawContent, Now, CancellationToken.None);

        var superseded = await store.StoreAsync(second, RawContent, Now, CancellationToken.None);

        Assert.True(superseded);
        Assert.Equal(ImportProposalStatus.Superseded, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(first.Id, CancellationToken.None));
        Assert.Equal(second.Id, (await store.FindPendingAsync(Participant, Inventory, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task One_Inventorys_pending_proposal_is_invisible_to_another_Inventory_and_to_another_Participant()
    {
        var store = new InMemoryImportProposalStore();
        await store.StoreAsync(Proposal(), RawContent, Now, CancellationToken.None);

        Assert.Null(await store.FindPendingAsync(Participant, new InventoryId(Guid.NewGuid()), CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(new ParticipantId(Guid.NewGuid()), Inventory, CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_first_caller_settles_a_pending_proposal_and_settling_discards_its_raw_content()
    {
        var store = new InMemoryImportProposalStore();
        var proposal = Proposal();
        await store.StoreAsync(proposal, RawContent, Now, CancellationToken.None);

        Assert.True(await store.SettleAsync(proposal.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None));
        Assert.False(await store.SettleAsync(proposal.Id, ImportProposalStatus.Confirmed, Now, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Rejected, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(Participant, Inventory, CancellationToken.None));
    }

    [Fact]
    public async Task Expiring_settles_only_proposals_whose_lifetime_has_run_out_and_is_bounded_by_maxRows()
    {
        var store = new InMemoryImportProposalStore();
        var first = Proposal();
        var second = Proposal(inventoryId: new InventoryId(Guid.NewGuid()));
        await store.StoreAsync(first, RawContent, Now, CancellationToken.None);
        await store.StoreAsync(second, RawContent, Now, CancellationToken.None);

        // Before either proposal's ten minutes are up, nothing expires.
        Assert.Equal(0, await store.ExpirePendingBeforeAsync(Now.AddMinutes(9), 100, CancellationToken.None));

        // Both are now overdue, but the bound admits only one.
        var expiredAt = Now.AddMinutes(10);
        Assert.Equal(1, await store.ExpirePendingBeforeAsync(expiredAt, 1, CancellationToken.None));

        // The remaining one is settled by a second, unbounded pass.
        Assert.Equal(1, await store.ExpirePendingBeforeAsync(expiredAt, 100, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Expired, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Equal(ImportProposalStatus.Expired, await store.FindStatusAsync(second.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(first.Id, CancellationToken.None));
        Assert.Null(await store.FindRawContentAsync(second.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_settled_proposals_only_deletes_those_past_cutoff_and_is_bounded_by_maxRows()
    {
        var store = new InMemoryImportProposalStore();
        var first = Proposal();
        var second = Proposal(inventoryId: new InventoryId(Guid.NewGuid()));
        await store.StoreAsync(first, RawContent, Now, CancellationToken.None);
        await store.StoreAsync(second, RawContent, Now, CancellationToken.None);
        await store.SettleAsync(first.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);
        await store.SettleAsync(second.Id, ImportProposalStatus.Rejected, Now, CancellationToken.None);

        // Not yet past the cutoff: neither is deleted.
        Assert.Equal(0, await store.DeleteSettledBeforeAsync(Now.AddMinutes(-1), 100, CancellationToken.None));

        // Both are past the cutoff, but the bound admits only one.
        Assert.Equal(1, await store.DeleteSettledBeforeAsync(Now, 1, CancellationToken.None));

        // The remaining one is deleted by a second, unbounded pass.
        Assert.Equal(1, await store.DeleteSettledBeforeAsync(Now, 100, CancellationToken.None));
        Assert.Null(await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Null(await store.FindStatusAsync(second.Id, CancellationToken.None));
    }
}
