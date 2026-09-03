using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class StockToolDispatcherTests
{
    private static readonly ParticipantId Viewer = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly UnitId EachUnit = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly ChannelConversationId SomeConversation = new("conversation-1");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StockEntrySummary Row(string name, decimal quantity, string idHex) => new(
        new StockEntryId(Guid.Parse($"{idHex}-0000-0000-0000-000000000000")),
        name,
        NameNormalization.Normalize(name),
        EachUnit,
        "each",
        null,
        null,
        null,
        Quantity.Create(quantity));

    private static (StockToolDispatcher Dispatcher, InMemoryStockStore StockStore) CreateDispatcher()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, Viewer, MembershipRole.Viewer, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();
        var dispatcher = new StockToolDispatcher(
            new StockListingService(stockStore, authorizationService),
            new StockFindingService(stockStore, authorizationService));

        return (dispatcher, stockStore);
    }

    private static TurnExecutionContext Context(ParticipantId participantId, InventoryId? activeInventoryId) => new(
        TurnId.NewId(), participantId, SomeConversation, new FoundryConversationId(Guid.NewGuid()), activeInventoryId, TraceId: null);

    [Fact]
    public async Task List_stock_tool_call_returns_a_completed_decision_with_a_typed_payload()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Completed, decision.Status);
        Assert.NotNull(decision.Payload);
        Assert.Contains("\"kind\":\"stock_list\"", decision.Payload);
        Assert.Contains("Bolts", decision.Payload);
    }

    [Fact]
    public async Task List_stock_include_zero_untrusted_arg_is_honored_as_a_filter_not_identity()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Nuts", 0m, "10000000"));
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string> { ["includeZero"] = "true" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Contains("Nuts", decision.Payload);
    }

    // The single highest-value security property of the whole dispatch seam: even if the untrusted
    // model-proposed args smuggled a participantId/inventoryId key (as a malicious or buggy model
    // might), the dispatcher must still act only on the TRUSTED TurnExecutionContext - never on
    // anything the args dictionary contains - so a hostile proposal can never widen access.
    [Fact]
    public async Task Malicious_untrusted_args_claiming_a_different_participant_or_inventory_are_ignored()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var maliciousArgs = new Dictionary<string, string>
        {
            ["participantId"] = Stranger.ToString(),
            ["inventoryId"] = Guid.NewGuid().ToString(),
            ["includeZero"] = "false",
        };
        var proposal = new ToolCallProposal("list_stock", maliciousArgs);

        // Trusted context still says Viewer/SomeInventory - the dispatcher must use exactly that,
        // never the maliciously-claimed identity in the args.
        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Completed, decision.Status);
        Assert.Contains("Bolts", decision.Payload);
    }

    [Fact]
    public async Task With_no_active_inventory_selected_the_tool_call_fails_without_ever_reaching_the_store()
    {
        var (dispatcher, _) = CreateDispatcher();
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, activeInventoryId: null), Now, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Failed, decision.Status);
        Assert.Equal("no_active_inventory", decision.Code);
    }

    [Fact]
    public async Task A_non_member_gets_a_non_disclosing_failure_never_revealing_the_inventory_exists()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Stranger, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Failed, decision.Status);
        Assert.Equal("not_found", decision.Code);
        Assert.DoesNotContain("Bolts", decision.Summary);
    }

    [Fact]
    public async Task Find_stock_tool_call_returns_ambiguous_candidates_in_the_payload()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 1m, "10000000"));
        stockStore.Add(SomeInventory, Row("Bolts", 1m, "20000000"));
        var proposal = new ToolCallProposal("find_stock", new Dictionary<string, string> { ["reference"] = "Bolts" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Completed, decision.Status);
        Assert.Equal("ambiguous", decision.Code);
        Assert.Contains("\"kind\":\"stock_find\"", decision.Payload);
    }

    [Fact]
    public async Task An_unrecognized_tool_name_fails_without_touching_any_store()
    {
        var (dispatcher, _) = CreateDispatcher();
        var proposal = new ToolCallProposal("delete_everything", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Failed, decision.Status);
        Assert.Equal("unknown_tool", decision.Code);
    }
}
