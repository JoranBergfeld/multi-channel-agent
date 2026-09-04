using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class ConfirmationProposalLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.NewGuid());
    private static readonly InventoryId SomeInventory = new(Guid.NewGuid());
    private static readonly InventoryId AnotherInventory = new(Guid.NewGuid());
    private const string Conversation = "conversation-1";

    private static ConfirmationProposal Proposal(string? conversation = null)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Participant,
            conversation ?? Conversation,
            SomeInventory,
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", new UnitId(Guid.NewGuid()), "each",
                        null, null, null, Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    private static TurnExecutionContext Context(
        InventoryId? activeInventoryId, bool wasInterrupted = false, string? conversation = null) => new(
        TurnId.NewId(),
        Participant,
        new ChannelConversationId(conversation ?? Conversation),
        new FoundryConversationId(Guid.NewGuid()),
        1,
        activeInventoryId,
        TraceId: null,
        DirectConfirmationEvidence.None,
        wasInterrupted);

    private static async Task<(InMemoryConfirmationProposalStore Store, ConfirmationProposal Proposal)> PendingAsync(
        string? conversation = null)
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal(conversation);
        await store.StoreAsync(proposal, Now, CancellationToken.None);

        return (store, proposal);
    }

    [Fact]
    public async Task An_interrupted_Turn_invalidates_whatever_was_pending_in_that_conversation()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await new ConfirmationProposalLifecycle(store).ReconcileAsync(
            Context(SomeInventory, wasInterrupted: true), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.Interrupted, settled);
        Assert.Equal(ProposalStatus.Interrupted, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_interrupted_Turn_leaves_other_conversations_proposals_alone()
    {
        var (store, mine) = await PendingAsync();
        var other = Proposal("conversation-2");
        await store.StoreAsync(other, Now, CancellationToken.None);

        await new ConfirmationProposalLifecycle(store).ReconcileAsync(
            Context(SomeInventory, wasInterrupted: true), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.Interrupted, await store.FindStatusAsync(mine.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await store.FindStatusAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Losing_access_to_the_Inventory_invalidates_the_pending_proposal()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await new ConfirmationProposalLifecycle(store).ReconcileAsync(
            Context(activeInventoryId: null), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.AccessLost, settled);
        Assert.Equal(ProposalStatus.AccessLost, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_Turn_whose_Active_Inventory_is_now_a_different_one_invalidates_the_pending_proposal()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await new ConfirmationProposalLifecycle(store).ReconcileAsync(
            Context(AnotherInventory), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.InventorySwitched, settled);
        Assert.Equal(ProposalStatus.InventorySwitched, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_ordinary_Turn_in_the_same_Inventory_leaves_the_pending_proposal_alone()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await new ConfirmationProposalLifecycle(store).ReconcileAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.Null(settled);
        Assert.Equal(ProposalStatus.Pending, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
}
