using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class TurnExecutionContextFactoryTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (TurnExecutionContextFactory Factory, InMemoryInventoryStore InventoryStore, InMemoryActiveInventorySelectionStore SelectionStore)
        CreateFactory()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        var selectionStore = new InMemoryActiveInventorySelectionStore();
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(selectionStore);
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var selectionService = new InventorySelectionService(authorizationService, selectionStore, new InMemoryConfirmationProposalStore());
        var bindingStore = new InMemoryFoundryConversationBindingStore();

        return (new TurnExecutionContextFactory(bindingStore, selectionService), inventoryStore, selectionStore);
    }

    private static InboundTurn Turn(string conversationId, string nativeMessageId = "native-1") =>
        TestTurns.Text(nativeMessageId, SomeParticipant, conversationId, "list stock", null, Now, "trace-1");

    [Fact]
    public async Task Assembles_a_context_carrying_the_turns_own_identity()
    {
        var (factory, _, _) = CreateFactory();
        var turn = Turn("conversation-1");

        var context = await factory.CreateAsync(turn, Now, CancellationToken.None);

        Assert.Equal(turn.TurnId, context.TurnId);
        Assert.Equal(turn.ParticipantId, context.ParticipantId);
        Assert.Equal(turn.ChannelConversationId, context.ChannelConversationId);
        Assert.Equal("trace-1", context.TraceId);
    }

    [Fact]
    public async Task Reuses_the_same_foundry_conversation_across_turns_in_the_same_channel_conversation()
    {
        var (factory, _, _) = CreateFactory();

        var first = await factory.CreateAsync(Turn("conversation-1", "native-1"), Now, CancellationToken.None);
        var second = await factory.CreateAsync(Turn("conversation-1", "native-2"), Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(first.FoundryConversationId, second.FoundryConversationId);
    }

    [Fact]
    public async Task Assigns_a_distinct_foundry_conversation_for_a_different_channel_conversation()
    {
        var (factory, _, _) = CreateFactory();

        var first = await factory.CreateAsync(Turn("conversation-1"), Now, CancellationToken.None);
        var second = await factory.CreateAsync(Turn("conversation-2"), Now, CancellationToken.None);

        Assert.NotEqual(first.FoundryConversationId, second.FoundryConversationId);
    }

    [Fact]
    public async Task With_no_active_inventory_selected_the_context_carries_none()
    {
        var (factory, _, _) = CreateFactory();

        var context = await factory.CreateAsync(Turn("conversation-1"), Now, CancellationToken.None);

        Assert.Null(context.ActiveInventoryId);
    }

    [Fact]
    public async Task Reflects_the_currently_authorized_active_inventory_selection()
    {
        var (factory, inventoryStore, selectionStore) = CreateFactory();
        inventoryStore.GrantMembership(SomeInventory, SomeParticipant, MembershipRole.Viewer, Now);
        await selectionStore.UpsertAsync(new ActiveInventorySelection(SomeParticipant, "conversation-1", SomeInventory, Now), CancellationToken.None);

        var context = await factory.CreateAsync(Turn("conversation-1"), Now, CancellationToken.None);

        Assert.Equal(SomeInventory, context.ActiveInventoryId);
    }

    // Access loss must be re-checked every time, never trusted from a stale selection - exactly the
    // same non-disclosing recheck InventorySelectionService already guarantees for the web BFF.
    [Fact]
    public async Task An_active_selection_whose_membership_was_since_revoked_is_not_reflected()
    {
        var (factory, inventoryStore, selectionStore) = CreateFactory();
        inventoryStore.GrantMembership(SomeInventory, SomeParticipant, MembershipRole.Viewer, Now);
        await selectionStore.UpsertAsync(new ActiveInventorySelection(SomeParticipant, "conversation-1", SomeInventory, Now), CancellationToken.None);
        inventoryStore.RevokeMembership(SomeInventory, SomeParticipant);

        var context = await factory.CreateAsync(Turn("conversation-1"), Now, CancellationToken.None);

        Assert.Null(context.ActiveInventoryId);
    }
    [Fact]
    public async Task The_trusted_context_carries_the_Turns_own_confirmation_evidence()
    {
        var (factory, _, _) = CreateFactory();
        var turn = ConfirmationTurn("confirm");

        var context = await factory.CreateAsync(turn, Now, CancellationToken.None);

        Assert.Equal(DirectConfirmationEvidence.Confirmed, context.Confirmation);
        Assert.False(context.WasInterrupted);
    }

    [Fact]
    public async Task An_interrupted_Turn_reaches_tool_dispatch_marked_as_such_and_confirming_nothing()
    {
        var (factory, _, _) = CreateFactory();
        var turn = ConfirmationTurn("confirm", wasInterrupted: true);

        var context = await factory.CreateAsync(turn, Now, CancellationToken.None);

        Assert.Equal(DirectConfirmationEvidence.None, context.Confirmation);
        Assert.True(context.WasInterrupted);
    }

    private static InboundTurn ConfirmationTurn(string contentText, bool wasInterrupted = false) =>
        InboundTurn.Create(InboundTurnDraft.DirectText(
            "native-confirm",
            SomeParticipant,
            "conversation-1",
            "web",
            ChannelPrincipal.EntraUser(SomeParticipant.Value.ToString(), "22222222-2222-2222-2222-222222222222"),
            ChannelCapabilities.Text,
            contentText,
            locale: null,
            Now,
            traceId: null,
            wasInterrupted));
}
