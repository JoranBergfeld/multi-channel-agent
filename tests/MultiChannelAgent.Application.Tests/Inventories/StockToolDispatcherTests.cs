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
    private static readonly LocationId ShelfA = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly ParticipantId Editor = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));
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
        var (dispatcher, stockStore, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Viewer);
        return (dispatcher, stockStore);
    }

    private static (StockToolDispatcher Dispatcher, InMemoryStockStore StockStore, InMemoryStockMutationStore MutationStore)
        CreateDispatcherWithMutations(ParticipantId participantId, MembershipRole role)
    {
        var full = CreateFullDispatcher(participantId, role);

        return (full.Dispatcher, full.StockStore, full.MutationStore);
    }

    private sealed record DispatcherHarness(
        StockToolDispatcher Dispatcher,
        InMemoryStockStore StockStore,
        InMemoryStockMutationStore MutationStore,
        InMemoryConfirmationProposalStore ProposalStore,
        InMemoryStockChangeSetStore ChangeSetStore,
        InMemoryInventoryReferenceStore ReferenceStore);

    private static DispatcherHarness CreateFullDispatcher(ParticipantId participantId, MembershipRole role)
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        inventoryStore.GrantMembership(SomeInventory, participantId, role, Now);
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(new InMemoryActiveInventorySelectionStore());
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
        referenceStore.AddUnit(SomeInventory, EachUnit, "each", "piece", "pieces", "pc", "pcs");
        var mutationStore = new InMemoryStockMutationStore(stockStore);
        mutationStore.NameUnit(EachUnit, "each");

        referenceStore.AddLocation(SomeInventory, ShelfA, "Shelf A");

        var proposalStore = new InMemoryConfirmationProposalStore();
        var changeSetStore = new InMemoryStockChangeSetStore(stockStore, proposalStore);

        var dispatcher = new StockToolDispatcher(
            new StockListingService(stockStore, referenceStore, authorizationService),
            new StockFindingService(stockStore, referenceStore, authorizationService),
            new StockMutationService(stockStore, mutationStore, referenceStore, authorizationService),
            new StockChangeSetService(
                new StockChangeResolver(stockStore, referenceStore), changeSetStore, proposalStore, authorizationService),
            new StockConfirmationService(proposalStore, changeSetStore, authorizationService));

        return new DispatcherHarness(dispatcher, stockStore, mutationStore, proposalStore, changeSetStore, referenceStore);
    }

    private static TurnExecutionContext Context(ParticipantId participantId, InventoryId? activeInventoryId) => new(
        TurnId.NewId(), participantId, SomeConversation, new FoundryConversationId(Guid.NewGuid()), FoundryConversationGeneration: 1, activeInventoryId, TraceId: null);

    /// <summary>A trusted context carrying this Turn's own confirmation evidence, and optionally a fixed Turn identity for replay.</summary>
    private static TurnExecutionContext ConfirmingContext(
        ParticipantId participantId, DirectConfirmationEvidence evidence, TurnId? turnId = null) => new(
        turnId ?? TurnId.NewId(),
        participantId,
        SomeConversation,
        new FoundryConversationId(Guid.NewGuid()),
        FoundryConversationGeneration: 1,
        SomeInventory,
        TraceId: null,
        evidence);

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

        // Names the reference actually at fault, so the Participant corrects that one.
        Assert.Equal("That Location does not exist in this Inventory.", decision.Summary);
    }

    [Fact]
    public async Task Add_stock_tool_call_returns_a_completed_decision_with_a_typed_mutation_payload()
    {
        var (dispatcher, _, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Steel Bolts", ["quantity"] = "12.5" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("\"kind\":\"stock_mutation\"", decision.Payload);
        Assert.Contains("\"operation\":\"add\"", decision.Payload);
        Assert.Contains("\"quantity\":\"12.5\"", decision.Payload);
        Assert.Single(decision.Deliveries);
    }

    [Fact]
    public async Task Remove_stock_tool_call_that_underflows_returns_a_conflict_that_changed_nothing()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 3m, "10000000"));
        var proposal = new ToolCallProposal(
            "remove_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "4" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Conflict, decision.Category);
        Assert.Equal("insufficient_quantity", decision.Code);
        Assert.Null(decision.Payload);
        Assert.Empty(mutationStore.AuditFacts);
    }

    [Fact]
    public async Task Set_stock_to_zero_returns_confirmation_required_rather_than_clearing_stock()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 7m, "10000000"));
        var proposal = new ToolCallProposal(
            "set_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "0" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        Assert.Equal("confirmation_required", decision.Code);
        Assert.Empty(mutationStore.AuditFacts);
    }

    [Fact]
    public async Task A_Viewer_proposing_a_mutation_is_refused_without_the_Inventory_being_touched()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Viewer);
        stockStore.Add(SomeInventory, Row("Bolts", 3m, "10000000"));
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "1" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Forbidden, decision.Category);
        Assert.Empty(mutationStore.AuditFacts);
    }

    [Fact]
    public async Task An_ambiguous_mutation_reference_is_answered_with_the_same_candidate_payload_a_Find_uses()
    {
        var (dispatcher, stockStore, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 3m, "10000000"));
        stockStore.Add(SomeInventory, Row("Bolts", 4m, "20000000") with { LocationId = new LocationId(Guid.NewGuid()), LocationName = "Shelf A" });
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "1" });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Ambiguous, decision.Category);
        Assert.Contains("\"kind\":\"stock_find\"", decision.Payload);
    }

    // The same security property the read tools already guarantee: identity comes only from the
    // trusted TurnExecutionContext, so args claiming another Participant or Inventory change nothing.
    [Fact]
    public async Task Malicious_untrusted_mutation_args_claiming_another_participant_or_inventory_are_ignored()
    {
        var (dispatcher, _, _) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        var proposal = new ToolCallProposal("add_stock", new Dictionary<string, string>
        {
            ["reference"] = "Bolts",
            ["quantity"] = "1",
            ["participantId"] = Stranger.ToString(),
            ["inventoryId"] = Guid.NewGuid().ToString(),
        });

        var decision = await dispatcher.DispatchAsync(proposal, Context(Viewer, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("\"stockEntryId\"", decision.Payload);
    }

    // Two dispatches of the SAME Turn and tool must derive the same operation identity, so the second
    // re-reports the first's effect rather than adding to stock again.
    [Fact]
    public async Task Dispatching_the_same_Turns_mutation_twice_never_applies_it_twice()
    {
        var (dispatcher, stockStore, mutationStore) = CreateDispatcherWithMutations(Viewer, MembershipRole.Editor);
        stockStore.Add(SomeInventory, Row("Bolts", 10m, "10000000"));
        var context = Context(Viewer, SomeInventory);
        var proposal = new ToolCallProposal(
            "add_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["quantity"] = "5" });

        var first = await dispatcher.DispatchAsync(proposal, context, Now, CancellationToken.None);
        var retry = await dispatcher.DispatchAsync(proposal, context, Now, CancellationToken.None);

        Assert.Contains("\"quantity\":\"15\"", first.Payload);
        Assert.Contains("\"quantity\":\"15\"", retry.Payload);
        Assert.Single(mutationStore.AuditFacts);
    }
    // ---- Move, Rename, Forget, batches, confirmation, and rejection (issue #32) ----

    private static StockEntrySummary SeedStock(
        DispatcherHarness harness, string name, decimal quantity, LocationId? locationId = null) =>
        harness.StockStore.CreateRow(
            SomeInventory,
            name,
            EachUnit,
            "each",
            locationId,
            locationId == ShelfA ? "Shelf A" : null,
            note: null,
            Quantity.Create(quantity));

    [Fact]
    public async Task Move_stock_transfers_and_reports_the_exact_read_back()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var source = SeedStock(harness, "Bolts", 10m);
        var proposal = new ToolCallProposal(
            "move_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["all"] = "true", ["to"] = "Shelf A" });

        var decision = await harness.Dispatcher.DispatchAsync(proposal, Context(Editor, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("\"kind\":\"stock_changes\"", decision.Payload);
        Assert.Contains("\"effect\":\"placed\"", decision.Payload);
        Assert.Contains(source.Id.ToString(), decision.Payload);
        Assert.Equal(ShelfA, harness.StockStore.Find(SomeInventory, source.Id)!.LocationId);
    }

    [Fact]
    public async Task Move_stock_that_would_retire_its_source_answers_confirmation_required_with_an_exact_proposal()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var source = SeedStock(harness, "Bolts", 10m);
        var destination = SeedStock(harness, "Bolts", 4m, ShelfA);
        var proposal = new ToolCallProposal(
            "move_stock",
            new Dictionary<string, string>
            {
                ["reference"] = "Bolts", ["unlocated"] = "true", ["all"] = "true", ["to"] = "Shelf A",
            });

        var decision = await harness.Dispatcher.DispatchAsync(proposal, Context(Editor, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        Assert.Equal("confirmation_required", decision.Code);
        Assert.Contains("\"kind\":\"stock_proposal\"", decision.Payload);
        Assert.Contains("\"token\":", decision.Payload);
        Assert.Contains("\"expiresAt\":", decision.Payload);
        Assert.Contains($"\"survivingStockEntryId\":\"{destination.Id}\"", decision.Payload);
        Assert.Contains($"\"retiredStockEntryId\":\"{source.Id}\"", decision.Payload);
        Assert.DoesNotContain(SomeInventory.ToString(), decision.Summary);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, source.Id)!.Quantity.ToInvariantText());
    }

    [Fact]
    public async Task Rename_stock_preserves_identity_and_reports_the_new_name()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var source = SeedStock(harness, "Bolts", 4m);
        var proposal = new ToolCallProposal(
            "rename_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["newName"] = "Steel Bolts" });

        var decision = await harness.Dispatcher.DispatchAsync(proposal, Context(Editor, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("\"effect\":\"renamed\"", decision.Payload);
        Assert.Contains("\"newName\":\"Steel Bolts\"", decision.Payload);
        Assert.Equal("Steel Bolts", harness.StockStore.Find(SomeInventory, source.Id)!.Name);
    }

    [Fact]
    public async Task Forget_stock_always_answers_confirmation_required()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var empty = SeedStock(harness, "Bolts", 0m);
        var proposal = new ToolCallProposal("forget_stock", new Dictionary<string, string> { ["reference"] = "Bolts" });

        var decision = await harness.Dispatcher.DispatchAsync(proposal, Context(Editor, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        Assert.Contains("\"effect\":\"forgotten\"", decision.Payload);
        Assert.NotNull(harness.StockStore.Find(SomeInventory, empty.Id));
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
    }

    [Fact]
    public async Task Apply_stock_changes_proposes_the_whole_batch_atomically()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var first = SeedStock(harness, "Bolts", 10m);
        var second = SeedStock(harness, "Rivets", 6m);
        var proposal = new ToolCallProposal(
            "apply_stock_changes",
            new Dictionary<string, string>
            {
                ["changes"] = """[{"kind":"add","reference":"Bolts","quantity":"1"},{"kind":"remove","reference":"Rivets","quantity":"2"}]""",
            });

        var decision = await harness.Dispatcher.DispatchAsync(proposal, Context(Editor, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        Assert.Contains("\"kind\":\"stock_proposal\"", decision.Payload);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, first.Id)!.Quantity.ToInvariantText());
        Assert.Equal("6", harness.StockStore.Find(SomeInventory, second.Id)!.Quantity.ToInvariantText());
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
    }

    [Fact]
    public async Task Apply_stock_changes_refuses_a_malformed_changes_argument_without_touching_Stock()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var untouched = SeedStock(harness, "Bolts", 10m);
        var proposal = new ToolCallProposal(
            "apply_stock_changes",
            new Dictionary<string, string> { ["changes"] = """[{"kind":"add","reference":"Bolts","participantId":"me"}]""" });

        var decision = await harness.Dispatcher.DispatchAsync(proposal, Context(Editor, SomeInventory), Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("invalid_changes", decision.Code);
        Assert.Equal("10", harness.StockStore.Find(SomeInventory, untouched.Id)!.Quantity.ToInvariantText());
        Assert.Null(await harness.ProposalStore.FindPendingAsync(Editor, SomeConversation.Value, CancellationToken.None));
    }

    [Fact]
    public async Task Confirm_inventory_operation_executes_only_when_the_Turn_itself_confirmed()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var token = await ProposeForgetAsync(harness);

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("confirm_inventory_operation", new Dictionary<string, string> { ["token"] = token }),
            ConfirmingContext(Editor, DirectConfirmationEvidence.Confirmed),
            Now,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Contains("\"kind\":\"stock_changes\"", decision.Payload);
        Assert.Contains("\"effect\":\"forgotten\"", decision.Payload);
        Assert.Single(harness.ChangeSetStore.AuditFacts);
    }

    [Fact]
    public async Task Confirm_inventory_operation_proposed_by_the_model_alone_executes_nothing()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var token = await ProposeForgetAsync(harness);
        var pending = (await harness.ProposalStore.FindPendingAsync(Editor, SomeConversation.Value, CancellationToken.None))!;

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("confirm_inventory_operation", new Dictionary<string, string> { ["token"] = token }),
            ConfirmingContext(Editor, DirectConfirmationEvidence.None),
            Now,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.Invalid, decision.Category);
        Assert.Equal("confirmation_evidence_missing", decision.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal(ProposalStatus.Pending, await harness.ProposalStore.FindStatusAsync(pending.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Reject_inventory_operation_settles_the_proposal_when_the_Turn_itself_rejected()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var token = await ProposeForgetAsync(harness);
        var pending = (await harness.ProposalStore.FindPendingAsync(Editor, SomeConversation.Value, CancellationToken.None))!;

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("reject_inventory_operation", new Dictionary<string, string> { ["token"] = token }),
            ConfirmingContext(Editor, DirectConfirmationEvidence.Rejected),
            Now,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Equal("rejected", decision.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
        Assert.Equal(ProposalStatus.Rejected, await harness.ProposalStore.FindStatusAsync(pending.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Every_new_tool_derives_its_operation_identity_from_the_Turn_and_never_from_its_arguments()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var source = SeedStock(harness, "Bolts", 10m);
        var turnId = TurnId.NewId();
        var context = ConfirmingContext(Editor, DirectConfirmationEvidence.None, turnId);
        var proposal = new ToolCallProposal(
            "move_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["all"] = "true", ["to"] = "Shelf A" });

        var first = await harness.Dispatcher.DispatchAsync(proposal, context, Now, CancellationToken.None);

        // The same Turn re-driven, with arguments that would now refuse: the ledger answers instead.
        var replay = await harness.Dispatcher.DispatchAsync(proposal, context, Now, CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, first.Category);
        Assert.Equal(OutcomeCategory.Completed, replay.Category);
        Assert.Equal(first.Payload, replay.Payload);
        Assert.Single(harness.ChangeSetStore.AuditFacts);
        Assert.Equal(ShelfA, harness.StockStore.Find(SomeInventory, source.Id)!.LocationId);
    }

    [Fact]
    public async Task A_proposal_payload_never_carries_a_row_version_an_audit_id_or_a_proposal_identity()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        SeedStock(harness, "Bolts", 0m);

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("forget_stock", new Dictionary<string, string> { ["reference"] = "Bolts" }),
            Context(Editor, SomeInventory),
            Now,
            CancellationToken.None);

        var pending = (await harness.ProposalStore.FindPendingAsync(Editor, SomeConversation.Value, CancellationToken.None))!;

        Assert.DoesNotContain("concurrencyStamp", decision.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proposalId", decision.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rowVersion", decision.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auditId", decision.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(pending.Id.ToString(), decision.Payload, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Proposes a Forget of an empty Stock Entry and returns the one-time token the answer carried.</summary>
    private static async Task<string> ProposeForgetAsync(DispatcherHarness harness)
    {
        SeedStock(harness, "Bolts", 0m);

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("forget_stock", new Dictionary<string, string> { ["reference"] = "Bolts" }),
            Context(Editor, SomeInventory),
            Now,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.ConfirmationRequired, decision.Category);
        harness.ChangeSetStore.AuditFacts.Clear();

        using var payload = System.Text.Json.JsonDocument.Parse(decision.Payload!);
        return payload.RootElement.GetProperty("token").GetString()!;
    }
    [Fact]
    public async Task A_confirmation_summary_never_repeats_the_token_the_payload_already_carries()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        SeedStock(harness, "Bolts", 0m);

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("forget_stock", new Dictionary<string, string> { ["reference"] = "Bolts" }),
            Context(Editor, SomeInventory),
            Now,
            CancellationToken.None);

        using var payload = System.Text.Json.JsonDocument.Parse(decision.Payload!);
        var token = payload.RootElement.GetProperty("token").GetString()!;

        // The summary is the human sentence; the token belongs to the payload the client renders.
        // Repeating it here would copy a bearer secret into a second durable column for no gain.
        Assert.DoesNotContain(token, decision.Summary, StringComparison.Ordinal);
        Assert.Contains("confirm", decision.Summary, StringComparison.OrdinalIgnoreCase);

        // Exactly one copy of it reaches a client, and the delivered answer is that same payload.
        Assert.Equal(1, CountOccurrences(decision.Payload!, token));
        var delivery = Assert.Single(decision.Deliveries);
        Assert.Equal(1, CountOccurrences(delivery.Payload, token));
        Assert.DoesNotContain(token, decision.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_confirmation_payload_is_retained_only_while_its_proposal_could_still_be_confirmed()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        SeedStock(harness, "Bolts", 0m);

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("forget_stock", new Dictionary<string, string> { ["reference"] = "Bolts" }),
            Context(Editor, SomeInventory),
            Now,
            CancellationToken.None);

        // The payload carries the token, so it is retained for exactly the window in which the token
        // means anything - not the ordinary payload retention.
        Assert.Equal(TimeSpan.FromMinutes(ConfirmationProposal.LifetimeMinutes), decision.PayloadRetention);
    }

    [Fact]
    public async Task An_ordinary_answer_keeps_the_ordinary_payload_retention()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        SeedStock(harness, "Bolts", 10m);

        var decision = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("move_stock", new Dictionary<string, string> { ["reference"] = "Bolts", ["all"] = "true", ["to"] = "Shelf A" }),
            Context(Editor, SomeInventory),
            Now,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Null(decision.PayloadRetention);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
    [Fact]
    public async Task A_token_recovered_from_a_retained_answer_can_no_longer_confirm_once_it_has_been_used()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var token = await ProposeForgetAsync(harness);

        var first = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("confirm_inventory_operation", new Dictionary<string, string> { ["token"] = token }),
            ConfirmingContext(Editor, DirectConfirmationEvidence.Confirmed),
            Now,
            CancellationToken.None);

        // The answer that carried this token is durable, so it can be read again after the fact. That
        // is the whole residual exposure, and single use is what bounds it: the same token, presented
        // with genuine direct confirmation, from the same Participant and conversation, does nothing.
        var replayed = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("confirm_inventory_operation", new Dictionary<string, string> { ["token"] = token }),
            ConfirmingContext(Editor, DirectConfirmationEvidence.Confirmed),
            Now,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.Completed, first.Category);
        Assert.Equal(OutcomeCategory.NotFound, replayed.Category);
        Assert.Equal("proposal_not_found", replayed.Code);
        Assert.Single(harness.ChangeSetStore.AuditFacts);
    }

    [Fact]
    public async Task A_token_recovered_from_a_retained_answer_can_no_longer_confirm_once_its_proposal_has_expired()
    {
        var harness = CreateFullDispatcher(Editor, MembershipRole.Editor);
        var token = await ProposeForgetAsync(harness);

        var expired = await harness.Dispatcher.DispatchAsync(
            new ToolCallProposal("confirm_inventory_operation", new Dictionary<string, string> { ["token"] = token }),
            ConfirmingContext(Editor, DirectConfirmationEvidence.Confirmed),
            Now.AddMinutes(ConfirmationProposal.LifetimeMinutes),
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.Conflict, expired.Category);
        Assert.Equal("proposal_expired", expired.Code);
        Assert.Empty(harness.ChangeSetStore.AuditFacts);
    }
}
