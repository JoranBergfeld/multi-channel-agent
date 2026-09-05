using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles;
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
        InventoryId? activeInventoryId,
        bool wasInterrupted = false,
        string? conversation = null,
        int generation = 1,
        bool acceptedInSupersededConversation = false) => new(
        TurnId.NewId(),
        Participant,
        new ChannelConversationId(conversation ?? Conversation),
        new FoundryConversationId(Guid.NewGuid()),
        generation,
        activeInventoryId,
        TraceId: null,
        DirectConfirmationEvidence.None,
        wasInterrupted,
        acceptedInSupersededConversation);

    /// <summary>
    /// The lifecycle under test, over a binding store whose conversation is on whatever generation
    /// the test put it on. Tests that are not about conversation resets get a fresh store, which
    /// establishes generation 1 on first read - exactly the generation their contexts carry.
    /// </summary>
    private static ConfirmationProposalLifecycle Lifecycle(
        InMemoryConfirmationProposalStore store, InMemoryFoundryConversationBindingStore? bindings = null) =>
        new(store, bindings ?? new InMemoryFoundryConversationBindingStore());

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

        var settled = await Lifecycle(store).ReconcileAsync(
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

        await Lifecycle(store).ReconcileAsync(
            Context(SomeInventory, wasInterrupted: true), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.Interrupted, await store.FindStatusAsync(mine.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await store.FindStatusAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Losing_access_to_the_Inventory_invalidates_the_pending_proposal()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await Lifecycle(store).ReconcileAsync(
            Context(activeInventoryId: null), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.AccessLost, settled);
        Assert.Equal(ProposalStatus.AccessLost, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_Turn_whose_Active_Inventory_is_now_a_different_one_invalidates_the_pending_proposal()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await Lifecycle(store).ReconcileAsync(
            Context(AnotherInventory), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.InventorySwitched, settled);
        Assert.Equal(ProposalStatus.InventorySwitched, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task An_ordinary_Turn_in_the_same_Inventory_leaves_the_pending_proposal_alone()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await Lifecycle(store).ReconcileAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.Null(settled);
        Assert.Equal(ProposalStatus.Pending, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_Turn_accepted_in_a_conversation_that_has_since_been_reset_invalidates_the_pending_proposal()
    {
        var (store, proposal) = await PendingAsync();

        var settled = await Lifecycle(store).ReconcileAsync(
            Context(SomeInventory, acceptedInSupersededConversation: true), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.ConversationReset, settled);
        Assert.Equal(ProposalStatus.ConversationReset, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    /// <summary>
    /// Puts one pending proposal in a conversation whose binding has since rotated to a new
    /// generation, exactly as a deliberate "New conversation" leaves it - except that here nothing
    /// settled the proposal, which is the very hole the post-dispatch pass exists to close.
    /// </summary>
    private static async Task<(InMemoryConfirmationProposalStore Proposals, InMemoryFoundryConversationBindingStore Bindings, ConfirmationProposal Proposal)>
        RotatedSinceAcceptanceAsync()
    {
        var (proposals, proposal) = await PendingAsync();
        var bindings = new InMemoryFoundryConversationBindingStore();
        await bindings.GetOrCreateAsync(Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);
        bindings.Rotate(Participant, new ChannelConversationId(Conversation), Now);

        return (proposals, bindings, proposal);
    }

    // The single property the whole post-dispatch settlement exists for. The context was assembled
    // BEFORE the reset, so the flag it captured truthfully said "current" at the time - and by the
    // time the Turn's proposal was written, it no longer was. Trusting that captured answer would
    // leave a confirmable proposal in a conversation the Participant has already walked away from,
    // so the settlement re-reads the binding rather than believing what it was told.
    [Fact]
    public async Task A_conversation_rotated_after_the_context_was_assembled_still_settles_what_the_Turn_just_proposed()
    {
        var (proposals, bindings, proposal) = await RotatedSinceAcceptanceAsync();

        var settlement = await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory, acceptedInSupersededConversation: false), Now, CancellationToken.None);

        Assert.True(settlement.ConversationWasSuperseded);
        Assert.Equal(ProposalStatus.ConversationReset, settlement.Settled);
        Assert.Equal(ProposalStatus.ConversationReset, await proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_conversation_still_on_the_generation_the_Turn_was_accepted_under_leaves_its_proposal_confirmable()
    {
        var (proposals, proposal) = await PendingAsync();
        var bindings = new InMemoryFoundryConversationBindingStore();
        await bindings.GetOrCreateAsync(Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);

        var settlement = await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.False(settlement.ConversationWasSuperseded);
        Assert.Null(settlement.Settled);
        Assert.Equal(ProposalStatus.Pending, await proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    // A Turn that proposes nothing leaves nothing to settle - but the conversation it ran in has
    // still been left behind, and only the caller can decide what that means for its own answer.
    [Fact]
    public async Task A_reset_conversation_with_nothing_pending_still_reports_the_conversation_as_left_behind()
    {
        var proposals = new InMemoryConfirmationProposalStore();
        var bindings = new InMemoryFoundryConversationBindingStore();
        await bindings.GetOrCreateAsync(Participant, new ChannelConversationId(Conversation), Now, CancellationToken.None);
        bindings.Rotate(Participant, new ChannelConversationId(Conversation), Now);

        var settlement = await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        // Nothing was settled - and the conversation is still reported as left behind, because that
        // is the fact the Turn's own answer has to be shaped by.
        Assert.True(settlement.ConversationWasSuperseded);
        Assert.Null(settlement.Settled);
    }

    [Fact]
    public async Task Settling_a_superseded_conversation_leaves_another_conversations_proposal_alone()
    {
        var (proposals, bindings, mine) = await RotatedSinceAcceptanceAsync();
        var other = Proposal("conversation-2");
        await proposals.StoreAsync(other, Now, CancellationToken.None);

        await Lifecycle(proposals, bindings).SettleSupersededConversationAsync(
            Context(SomeInventory), Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.ConversationReset, await proposals.FindStatusAsync(mine.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await proposals.FindStatusAsync(other.Id, CancellationToken.None));
    }

    /// <summary>
    /// A binding store whose read fails, so a test can prove the settlement reports the fault rather
    /// than quietly treating a conversation it could not read as still current.
    /// </summary>
    private sealed class FailingBindingStore(Exception failure) : IFoundryConversationBindingStore
    {
        public Task<FoundryConversationBinding> GetOrCreateAsync(
            ParticipantId participantId,
            ChannelConversationId channelConversationId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            Task.FromException<FoundryConversationBinding>(failure);
    }

    [Fact]
    public async Task Settling_a_superseded_conversation_propagates_cancellation()
    {
        var (proposals, _) = await PendingAsync();
        var lifecycle = new ConfirmationProposalLifecycle(
            proposals, new FailingBindingStore(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.SettleSupersededConversationAsync(Context(SomeInventory), Now, CancellationToken.None));
    }

    // Failing to read the binding is not evidence that the conversation is still current. Swallowing
    // it would answer "nothing to settle" and leave a confirmable proposal behind; letting it out
    // leaves the whole Turn to be retried, which is the only safe reading of "we do not know".
    [Fact]
    public async Task A_binding_read_that_fails_is_never_swallowed_into_leaving_the_proposal_confirmable()
    {
        var (proposals, proposal) = await PendingAsync();
        var lifecycle = new ConfirmationProposalLifecycle(
            proposals, new FailingBindingStore(new InvalidOperationException("binding read failed")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.SettleSupersededConversationAsync(Context(SomeInventory), Now, CancellationToken.None));

        Assert.Equal(ProposalStatus.Pending, await proposals.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
}
