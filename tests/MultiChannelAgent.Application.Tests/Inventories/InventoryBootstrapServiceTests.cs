using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryBootstrapServiceTests
{
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string ConversationId = "web-conversation-1";

    private static (InventoryBootstrapService Service, InMemoryInventoryStore InventoryStore, InMemoryActiveInventorySelectionStore SelectionStore, InventoryCreationService Creation)
        CreateService()
    {
        var participantStore = new InMemoryParticipantStore();
        var inventoryStore = new InMemoryInventoryStore(_ => "Test Participant");
        var selectionStore = new InMemoryActiveInventorySelectionStore();

        var participantSession = new ParticipantSessionService(participantStore);
        var listing = new InventoryListingService(inventoryStore);
        var authorizationService = new InventoryAuthorizationService(inventoryStore, new InMemoryInventoryAuthorizationAuditStore(selectionStore));
        var selection = new InventorySelectionService(authorizationService, selectionStore, new InMemoryConfirmationProposalStore());
        var creation = new InventoryCreationService(inventoryStore);

        return (new InventoryBootstrapService(participantSession, listing, selection), inventoryStore, selectionStore, creation);
    }

    // A Participant with zero Memberships must receive explicit onboarding rather than a silently
    // auto-created Inventory.
    [Fact]
    public async Task Bootstrap_signals_onboarding_when_the_participant_has_no_memberships()
    {
        var (service, _, _, _) = CreateService();

        var view = await service.BootstrapAsync(Participant, "Test Participant", ConversationId, Now, CancellationToken.None);

        Assert.True(view.NeedsOnboarding);
        Assert.Empty(view.Inventories);
        Assert.Null(view.ActiveInventoryId);
        Assert.Equal("Test Participant", view.DisplayName);
        Assert.Equal(ConversationId, view.WebConversationId);
    }

    // A Participant with exactly one accessible Inventory should have it auto-selected for the
    // current conversation, so ordinary requests require no explicit setup step.
    [Fact]
    public async Task Bootstrap_auto_selects_the_single_accessible_inventory()
    {
        var (service, _, selectionStore, creation) = CreateService();
        var created = await creation.CreateAsync(Participant, "Test Participant", "Warehouse", "req-1", Now, CancellationToken.None);

        var view = await service.BootstrapAsync(Participant, "Test Participant", ConversationId, Now, CancellationToken.None);

        Assert.False(view.NeedsOnboarding);
        Assert.Equal(created.Id, view.ActiveInventoryId);
        Assert.True(selectionStore.Selections.ContainsKey((Participant, ConversationId)));
    }

    // With multiple accessible Inventories, the agent must never guess: no auto-selection happens
    // and the Participant must explicitly choose.
    [Fact]
    public async Task Bootstrap_does_not_auto_select_when_multiple_inventories_are_accessible()
    {
        var (service, _, selectionStore, creation) = CreateService();
        await creation.CreateAsync(Participant, "Test Participant", "Warehouse A", "req-1", Now, CancellationToken.None);
        await creation.CreateAsync(Participant, "Test Participant", "Warehouse B", "req-2", Now, CancellationToken.None);

        var view = await service.BootstrapAsync(Participant, "Test Participant", ConversationId, Now, CancellationToken.None);

        Assert.False(view.NeedsOnboarding);
        Assert.Equal(2, view.Inventories.Count);
        Assert.Null(view.ActiveInventoryId);
        Assert.False(selectionStore.Selections.ContainsKey((Participant, ConversationId)));
    }

    // Once explicitly switched, a later bootstrap must keep reporting that same Active Inventory
    // rather than re-deriving or clearing it.
    [Fact]
    public async Task Bootstrap_reports_a_previously_explicit_selection_among_multiple_inventories()
    {
        var (service, inventoryStore, selectionStore, creation) = CreateService();
        await creation.CreateAsync(Participant, "Test Participant", "Warehouse A", "req-1", Now, CancellationToken.None);
        var second = await creation.CreateAsync(Participant, "Test Participant", "Warehouse B", "req-2", Now, CancellationToken.None);
        var selectionService = new InventorySelectionService(
            new InventoryAuthorizationService(inventoryStore, new InMemoryInventoryAuthorizationAuditStore(selectionStore)),
            selectionStore,
            new InMemoryConfirmationProposalStore());
        await selectionService.SelectAsync(Participant, new InventoryId(Guid.Parse(second.Id)), ConversationId, Now, CancellationToken.None);

        var view = await service.BootstrapAsync(Participant, "Test Participant", ConversationId, Now, CancellationToken.None);

        Assert.Equal(second.Id, view.ActiveInventoryId);
    }
}
