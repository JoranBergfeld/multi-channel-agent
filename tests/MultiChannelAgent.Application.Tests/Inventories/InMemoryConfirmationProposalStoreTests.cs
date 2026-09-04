using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

/// <summary>
/// Pins the contract the double and SqlConfirmationProposalStore must both satisfy. The SQL twin of
/// these assertions lives in SqlConfirmationProposalStoreTests, where the same invariants are proved
/// against real relational constraints.
/// </summary>
public sealed class InMemoryConfirmationProposalStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.NewGuid());
    private static readonly InventoryId Inventory = new(Guid.NewGuid());
    private const string Conversation = "web:profile-1";

    private static ConfirmationProposal Proposal(string? conversation = null, ParticipantId? participantId = null)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            participantId ?? Participant,
            conversation ?? Conversation,
            Inventory,
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

    [Fact]
    public async Task A_stored_proposal_is_found_by_its_Participant_and_conversation()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();

        var replacement = await store.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.False(replacement.SupersededExisting);
        Assert.Equal(proposal.Id, (await store.FindPendingAsync(Participant, Conversation, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task Storing_a_new_proposal_supersedes_the_pending_one_in_that_conversation()
    {
        var store = new InMemoryConfirmationProposalStore();
        var first = Proposal();
        var second = Proposal();
        await store.StoreAsync(first, Now, CancellationToken.None);

        var replacement = await store.StoreAsync(second, Now, CancellationToken.None);

        Assert.True(replacement.SupersededExisting);
        Assert.Equal(ProposalStatus.Superseded, await store.FindStatusAsync(first.Id, CancellationToken.None));
        Assert.Equal(second.Id, (await store.FindPendingAsync(Participant, Conversation, CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task One_conversations_proposal_is_invisible_to_another_conversation_and_to_another_Participant()
    {
        var store = new InMemoryConfirmationProposalStore();
        await store.StoreAsync(Proposal(), Now, CancellationToken.None);

        Assert.Null(await store.FindPendingAsync(Participant, "web:profile-2", CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(new ParticipantId(Guid.NewGuid()), Conversation, CancellationToken.None));
    }

    [Fact]
    public async Task Two_conversations_may_each_hold_their_own_pending_proposal()
    {
        var store = new InMemoryConfirmationProposalStore();
        var first = Proposal();
        var second = Proposal("web:profile-2");

        await store.StoreAsync(first, Now, CancellationToken.None);
        await store.StoreAsync(second, Now, CancellationToken.None);

        Assert.Equal(first.Id, (await store.FindPendingAsync(Participant, Conversation, CancellationToken.None))!.Id);
        Assert.Equal(second.Id, (await store.FindPendingAsync(Participant, "web:profile-2", CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task Only_the_first_caller_settles_a_pending_proposal()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.True(await store.SettleAsync(proposal.Id, ProposalStatus.Rejected, Now, CancellationToken.None));
        Assert.False(await store.SettleAsync(proposal.Id, ProposalStatus.Confirmed, Now, CancellationToken.None));
        Assert.Equal(ProposalStatus.Rejected, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
        Assert.Null(await store.FindPendingAsync(Participant, Conversation, CancellationToken.None));
    }

    [Fact]
    public async Task Invalidating_the_pending_proposal_settles_exactly_the_one_in_that_conversation()
    {
        var store = new InMemoryConfirmationProposalStore();
        var mine = Proposal();
        var other = Proposal("web:profile-2");
        await store.StoreAsync(mine, Now, CancellationToken.None);
        await store.StoreAsync(other, Now, CancellationToken.None);

        var invalidated = await store.InvalidatePendingAsync(
            Participant, Conversation, ProposalStatus.InventorySwitched, Now, CancellationToken.None);

        Assert.Equal(1, invalidated);
        Assert.Equal(ProposalStatus.InventorySwitched, await store.FindStatusAsync(mine.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await store.FindStatusAsync(other.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Expiring_settles_only_proposals_whose_lifetime_has_run_out()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.Equal(0, await store.ExpirePendingBeforeAsync(Now.AddMinutes(9), 100, CancellationToken.None));
        Assert.Equal(1, await store.ExpirePendingBeforeAsync(Now.AddMinutes(10), 100, CancellationToken.None));
        Assert.Equal(ProposalStatus.Expired, await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Settled_proposals_are_deleted_only_once_they_are_past_retention()
    {
        var store = new InMemoryConfirmationProposalStore();
        var proposal = Proposal();
        await store.StoreAsync(proposal, Now, CancellationToken.None);
        await store.SettleAsync(proposal.Id, ProposalStatus.Rejected, Now, CancellationToken.None);

        // The sweep hands the store the settle-instant threshold it wants deleted - "now minus the
        // retention window" - so a proposal settled at Now survives a pass 23 hours later and is
        // deleted by one 25 hours later.
        var retention = TimeSpan.FromHours(24);

        Assert.Equal(0, await store.DeleteSettledBeforeAsync(Now.AddHours(23) - retention, 100, CancellationToken.None));
        Assert.Equal(1, await store.DeleteSettledBeforeAsync(Now.AddHours(25) - retention, 100, CancellationToken.None));
        Assert.Null(await store.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
}
