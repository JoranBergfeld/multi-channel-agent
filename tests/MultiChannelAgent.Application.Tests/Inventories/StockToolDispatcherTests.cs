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
        var referenceStore = new InMemoryInventoryReferenceStore();
        var dispatcher = new StockToolDispatcher(
            new StockListingService(stockStore, referenceStore, authorizationService),
            new StockFindingService(stockStore, referenceStore, authorizationService));

        return (dispatcher, stockStore);
    }

    private static TurnExecutionContext Context(ParticipantId participantId, InventoryId? activeInventoryId) => new(
        TurnId.NewId(), participantId, SomeConversation, new FoundryConversationId(Guid.NewGuid()), FoundryConversationGeneration: 1, activeInventoryId, TraceId: null);

    [Fact]
    public async Task List_stock_tool_call_returns_a_completed_decision_with_a_typed_payload()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
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

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("Bolts", decision.Payload);
    }

    [Fact]
    public async Task With_no_active_inventory_selected_the_tool_call_fails_without_ever_reaching_the_store()
    {
        var (dispatcher, _) = CreateDispatcher();
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, activeInventoryId: null), Now, CancellationToken.None);

        // Guidance the Participant can act on, not a system failure: processing completed and
        // answered, with an Invalid semantic category.
        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("no_active_inventory", decision.Code);
    }

    [Fact]
    public async Task A_non_member_gets_a_non_disclosing_failure_never_revealing_the_inventory_exists()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Stranger, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.NotFound, decision.Category);
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

        Assert.Equal(OutcomeCategory.Ambiguous, decision.Category);
        Assert.Equal("ambiguous", decision.Code);
        Assert.Contains("\"kind\":\"stock_find\"", decision.Payload);
    }

    [Fact]
    public async Task An_unrecognized_tool_name_fails_without_touching_any_store()
    {
        var (dispatcher, _) = CreateDispatcher();
        var proposal = new ToolCallProposal("delete_everything", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        // The model proposing something this application cannot execute IS a model failure.
        Assert.Equal(OutcomeCategory.TransientFailure, decision.Category);
        Assert.Equal("unknown_tool", decision.Code);
    }
    // A read answer is a response the Participant is owed, so it must leave a durable, channel-
    // neutral response part behind - the record Delivery dispatch (and its independent retries) work
    // from. Without one, a completed read produced nothing to send and the conversation silently
    // showed no answer at all.
    [Fact]
    public async Task A_list_answer_requests_exactly_one_channel_neutral_response_part()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        var response = Assert.Single(decision.Deliveries);
        Assert.Equal(StockToolDispatcher.ResponseChannel, response.Channel);
        Assert.Contains("Bolts", response.Payload);
    }

    [Fact]
    public async Task A_find_answer_requests_exactly_one_channel_neutral_response_part()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 5m, "10000000"));
        var proposal = new ToolCallProposal("find_stock", new Dictionary<string, string> { ["reference"] = "Bolts" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        var response = Assert.Single(decision.Deliveries);
        Assert.Equal(StockToolDispatcher.ResponseChannel, response.Channel);
    }

    // Semantic answers are answers too: the Participant must be told "nothing matched" or "select an
    // Inventory first", so those also leave a response part behind.
    [Fact]
    public async Task A_semantic_answer_still_requests_a_response_part()
    {
        var (dispatcher, _) = CreateDispatcher();
        var proposal = new ToolCallProposal("list_stock", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, activeInventoryId: null), Now, CancellationToken.None);

        var response = Assert.Single(decision.Deliveries);
        Assert.Equal(decision.Summary, response.Payload);
    }

    // A model/system failure has produced no answer yet and will be retried, so requesting a response
    // part for it would send the Participant something the retry then contradicts.
    [Fact]
    public async Task A_system_failure_requests_no_response_part()
    {
        var (dispatcher, _) = CreateDispatcher();
        var proposal = new ToolCallProposal("delete_everything", new Dictionary<string, string>());

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Empty(decision.Deliveries);
    }
    // Wording a Participant can trust: an answer that shows five of many must say so, and must offer
    // narrowing that would really change the result.
    [Fact]
    public async Task An_oversized_ambiguous_answer_says_plainly_that_more_matched_and_how_to_narrow()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        var boxUnit = new UnitId(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        for (var i = 0; i < 7; i++)
        {
            stockStore.Add(SomeInventory, new StockEntrySummary(
                new StockEntryId(Guid.Parse($"{i + 1:00000000}-0000-0000-0000-000000000000")),
                "Bolts",
                "bolts",
                i % 2 == 0 ? EachUnit : boxUnit,
                i % 2 == 0 ? "each" : "box",
                new LocationId(Guid.NewGuid()),
                $"Shelf {(char)('A' + i)}",
                null,
                Quantity.Create(1m)));
        }

        var proposal = new ToolCallProposal("find_stock", new Dictionary<string, string> { ["reference"] = "Bolts" });
        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Ambiguous, decision.Category);
        Assert.Contains("More than 5 Stock Entries match", decision.Summary);
        Assert.Contains("showing the first 5", decision.Summary);
        Assert.Contains("Narrow by", decision.Summary);
        Assert.Contains("\"hasMoreCandidates\":true", decision.Payload);
        Assert.Contains("narrowingHints", decision.Payload);
    }

    [Fact]
    public async Task A_small_ambiguous_answer_never_claims_more_matched_than_did()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 1m, "10000000"));
        stockStore.Add(SomeInventory, Row("Bolts", 1m, "20000000"));

        var proposal = new ToolCallProposal("find_stock", new Dictionary<string, string> { ["reference"] = "Bolts" });
        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal("2 Stock Entries match. Choose one.", decision.Summary);
        Assert.Contains("\"hasMoreCandidates\":false", decision.Payload);
    }

    [Fact]
    public async Task Find_narrowing_args_reach_the_deterministic_service_as_exact_references()
    {
        var (dispatcher, stockStore) = CreateDispatcher();
        stockStore.Add(SomeInventory, Row("Bolts", 1m, "10000000"));

        var proposal = new ToolCallProposal(
            "find_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["location"] = "Shelf Z" });
        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.NotFound, decision.Category);
        Assert.Equal("reference_not_found", decision.Code);
    }
}
