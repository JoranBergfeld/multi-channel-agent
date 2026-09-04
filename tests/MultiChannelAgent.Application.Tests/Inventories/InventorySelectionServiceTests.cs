using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventorySelectionServiceTests
{
    private static readonly ParticipantId Member = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId NonMember = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string ConversationId = "web-conversation-1";

    private static (InventorySelectionService Service, InMemoryInventoryStore InventoryStore, InMemoryActiveInventorySelectionStore SelectionStore, InventoryId InventoryId)
        CreateServiceWithOneInventory()
    {
        var (service, inventoryStore, selectionStore, _, inventoryId) = CreateServiceWithProposalStore();

        return (service, inventoryStore, selectionStore, inventoryId);
    }

    private static (InventorySelectionService Service, InMemoryInventoryStore InventoryStore, InMemoryActiveInventorySelectionStore SelectionStore, InMemoryConfirmationProposalStore ProposalStore, InventoryId InventoryId)
        CreateServiceWithProposalStore()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        var creation = new InventoryCreationService(inventoryStore);
        var view = creation.CreateAsync(Member, "Owner Name", "Warehouse", "req-1", Now, CancellationToken.None).GetAwaiter().GetResult();
        var selectionStore = new InMemoryActiveInventorySelectionStore();
        var authorizationService = new InventoryAuthorizationService(inventoryStore, new InMemoryInventoryAuthorizationAuditStore(selectionStore));
        var proposalStore = new InMemoryConfirmationProposalStore();
        var service = new InventorySelectionService(authorizationService, selectionStore, proposalStore);

        return (service, inventoryStore, selectionStore, proposalStore, new InventoryId(Guid.Parse(view.Id)));
    }

    [Fact]
    public async Task Selecting_an_authorized_inventory_succeeds_and_persists_the_selection()
    {
        var (service, _, selectionStore, inventoryId) = CreateServiceWithOneInventory();

        var result = await service.SelectAsync(Member, inventoryId, ConversationId, Now, CancellationToken.None);

        Assert.Equal(InventorySelectionOutcome.Selected, result.Outcome);
        Assert.Equal(inventoryId, result.InventoryId);
        Assert.True(selectionStore.Selections.ContainsKey((Member, ConversationId)));
    }

    // Selecting an Inventory the Participant is not a member of must return a non-disclosing
    // "not authorized" outcome - never a distinct "not found" vs "forbidden" signal that would let a
    // caller infer whether the Inventory exists - and must never itself create a Membership.
    [Fact]
    public async Task Selecting_an_unauthorized_inventory_is_not_authorized_and_grants_no_access()
    {
        var (service, inventoryStore, selectionStore, inventoryId) = CreateServiceWithOneInventory();

        var result = await service.SelectAsync(NonMember, inventoryId, ConversationId, Now, CancellationToken.None);

        Assert.Equal(InventorySelectionOutcome.NotAuthorized, result.Outcome);
        Assert.Null(result.InventoryId);
        Assert.False(selectionStore.Selections.ContainsKey((NonMember, ConversationId)));
        Assert.DoesNotContain(inventoryStore.Memberships, m => m.ParticipantId == NonMember);
    }

    [Fact]
    public async Task Selecting_a_nonexistent_inventory_id_is_not_authorized()
    {
        var (service, _, _, _) = CreateServiceWithOneInventory();
        var randomInventoryId = new InventoryId(Guid.NewGuid());

        var result = await service.SelectAsync(Member, randomInventoryId, ConversationId, Now, CancellationToken.None);

        Assert.Equal(InventorySelectionOutcome.NotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task GetActiveInventoryIdAsync_returns_null_when_nothing_is_selected()
    {
        var (service, _, _, _) = CreateServiceWithOneInventory();

        var active = await service.GetActiveInventoryIdAsync(Member, ConversationId, Now, CancellationToken.None);

        Assert.Null(active);
    }

    [Fact]
    public async Task GetActiveInventoryIdAsync_returns_the_selected_inventory_while_fresh()
    {
        var (service, _, _, inventoryId) = CreateServiceWithOneInventory();
        await service.SelectAsync(Member, inventoryId, ConversationId, Now, CancellationToken.None);

        var active = await service.GetActiveInventoryIdAsync(Member, ConversationId, Now.AddDays(10), CancellationToken.None);

        Assert.Equal(inventoryId, active);
    }

    // Active Inventory selection expires after 30 inactive days and must clear rather than silently
    // keep pointing at stale context.
    [Fact]
    public async Task GetActiveInventoryIdAsync_clears_and_returns_null_once_expired()
    {
        var (service, _, selectionStore, inventoryId) = CreateServiceWithOneInventory();
        await service.SelectAsync(Member, inventoryId, ConversationId, Now, CancellationToken.None);

        var active = await service.GetActiveInventoryIdAsync(Member, ConversationId, Now.AddDays(31), CancellationToken.None);

        Assert.Null(active);
        Assert.False(selectionStore.Selections.ContainsKey((Member, ConversationId)));
    }

    // Active Inventory selection must clear on access loss (revoked Membership) rather than continue
    // to reference an Inventory the Participant can no longer reach.
    [Fact]
    public async Task GetActiveInventoryIdAsync_clears_and_returns_null_after_membership_is_revoked()
    {
        var (service, inventoryStore, selectionStore, inventoryId) = CreateServiceWithOneInventory();
        await service.SelectAsync(Member, inventoryId, ConversationId, Now, CancellationToken.None);
        inventoryStore.RevokeMembership(inventoryId, Member);

        var active = await service.GetActiveInventoryIdAsync(Member, ConversationId, Now.AddDays(1), CancellationToken.None);

        Assert.Null(active);
        Assert.False(selectionStore.Selections.ContainsKey((Member, ConversationId)));
    }
    [Fact]
    public async Task Switching_the_Active_Inventory_invalidates_the_pending_proposal_in_that_conversation()
    {
        var (service, inventoryStore, _, proposalStore, inventoryId) = CreateServiceWithProposalStore();
        var creation = new InventoryCreationService(inventoryStore);
        var other = new InventoryId(Guid.Parse(
            (await creation.CreateAsync(Member, "Owner Name", "Annex", "req-2", Now, CancellationToken.None)).Id));

        await service.SelectAsync(Member, inventoryId, ConversationId, Now, CancellationToken.None);
        var proposal = PendingProposal(inventoryId);
        await proposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        await service.SelectAsync(Member, other, ConversationId, Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.InventorySwitched, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Selecting_the_Inventory_that_is_already_active_leaves_the_pending_proposal_alone()
    {
        var (service, _, _, proposalStore, inventoryId) = CreateServiceWithProposalStore();

        await service.SelectAsync(Member, inventoryId, ConversationId, Now, CancellationToken.None);
        var proposal = PendingProposal(inventoryId);
        await proposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        await service.SelectAsync(Member, inventoryId, ConversationId, Now, CancellationToken.None);

        Assert.Equal(ProposalStatus.Pending, await proposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    private static ConfirmationProposal PendingProposal(InventoryId inventoryId)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Member,
            ConversationId,
            inventoryId,
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
}
