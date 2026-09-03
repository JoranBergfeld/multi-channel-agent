using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryAuthorizationServiceTests
{
    private static readonly ParticipantId Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Viewer = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId NonMember = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string ConversationId = "web-conversation-1";

    private static (InventoryAuthorizationService Service, InMemoryInventoryStore InventoryStore, InMemoryActiveInventorySelectionStore SelectionStore, InMemoryInventoryAuthorizationAuditStore AuditStore)
        CreateService()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Owner, MembershipRole.Owner, Now);
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);

        var selectionStore = new InMemoryActiveInventorySelectionStore();
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(selectionStore);
        var service = new InventoryAuthorizationService(inventoryStore, auditStore);

        return (service, inventoryStore, selectionStore, auditStore);
    }

    [Fact]
    public async Task A_member_with_no_required_role_is_authorized()
    {
        var (service, _, _, _) = CreateService();

        var result = await service.AuthorizeAsync(Viewer, SomeInventory, requiredRole: null, ConversationId, Now, CancellationToken.None);

        Assert.Equal(InventoryAuthorizationOutcome.Authorized, result.Outcome);
        Assert.Equal(MembershipRole.Viewer, result.Role);
    }

    [Fact]
    public async Task A_non_member_is_not_found_and_produces_a_non_disclosing_audit_fact()
    {
        var (service, _, _, auditStore) = CreateService();

        var result = await service.AuthorizeAsync(NonMember, SomeInventory, requiredRole: null, ConversationId, Now, CancellationToken.None);

        Assert.Equal(InventoryAuthorizationOutcome.NotFound, result.Outcome);
        Assert.Null(result.Role);
        var fact = Assert.Single(auditStore.RecordedFacts);
        Assert.Equal(AuditEventType.AccessDenied, fact.EventType);
        Assert.Equal("Denied:NotAMember", fact.OutcomeCode);
        Assert.Equal(SomeInventory, fact.InventoryId);
    }

    [Fact]
    public async Task A_non_member_denial_clears_any_stale_active_inventory_selection_for_that_conversation()
    {
        var (service, inventoryStore, selectionStore, _) = CreateService();
        inventoryStore.GrantMembership(SomeInventory, NonMember, MembershipRole.Viewer, Now);
        await selectionStore.UpsertAsync(new ActiveInventorySelection(NonMember, ConversationId, SomeInventory, Now), CancellationToken.None);
        inventoryStore.RevokeMembership(SomeInventory, NonMember);

        var result = await service.AuthorizeAsync(NonMember, SomeInventory, requiredRole: null, ConversationId, Now, CancellationToken.None);

        Assert.Equal(InventoryAuthorizationOutcome.NotFound, result.Outcome);
        Assert.False(selectionStore.Selections.ContainsKey((NonMember, ConversationId)));
    }

    [Fact]
    public async Task A_viewer_does_not_satisfy_a_required_owner_role_and_is_forbidden_not_not_found()
    {
        var (service, _, _, auditStore) = CreateService();

        var result = await service.AuthorizeAsync(Viewer, SomeInventory, MembershipRole.Owner, channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(InventoryAuthorizationOutcome.Forbidden, result.Outcome);
        Assert.Equal(MembershipRole.Viewer, result.Role);
        var fact = Assert.Single(auditStore.RecordedFacts);
        Assert.Equal("Denied:InsufficientRole", fact.OutcomeCode);
    }

    [Fact]
    public async Task An_owner_satisfies_a_required_owner_role()
    {
        var (service, _, _, _) = CreateService();

        var result = await service.AuthorizeAsync(Owner, SomeInventory, MembershipRole.Owner, channelConversationId: null, Now, CancellationToken.None);

        Assert.Equal(InventoryAuthorizationOutcome.Authorized, result.Outcome);
    }

    // Being merely forbidden (a real, lesser-privileged member) never itself clears an Active
    // Inventory selection - that Participant still has legitimate access to that Inventory.
    [Fact]
    public async Task Insufficient_role_denial_does_not_clear_the_active_inventory_selection()
    {
        var (service, _, selectionStore, _) = CreateService();
        await selectionStore.UpsertAsync(new ActiveInventorySelection(Viewer, ConversationId, SomeInventory, Now), CancellationToken.None);

        await service.AuthorizeAsync(Viewer, SomeInventory, MembershipRole.Owner, ConversationId, Now, CancellationToken.None);

        Assert.True(selectionStore.Selections.ContainsKey((Viewer, ConversationId)));
    }
}
