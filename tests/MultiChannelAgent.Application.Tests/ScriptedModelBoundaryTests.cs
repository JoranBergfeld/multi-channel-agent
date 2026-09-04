using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class ScriptedModelBoundaryTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly ModelInvocationContext BoundConversation = new(
        new FoundryConversationId(Guid.Parse("99999999-9999-9999-9999-999999999999")), Generation: 1, Locale: null);

    private static InboundTurn Turn(string contentText) =>
        TestTurns.Text("native-1", SomeParticipant, "conversation-1", contentText, null, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task Ordinary_unrecognized_content_produces_a_direct_completed_outcome_with_one_echo_delivery()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("hello"), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        var decision = proposal.Direct!;
        Assert.Equal(OutcomeCategory.Completed, decision.Category);
        Assert.Equal("echoed", decision.Code);
        Assert.Equal("Echoed: hello", decision.Summary);
        var delivery = Assert.Single(decision.Deliveries);
        Assert.Equal("synthetic", delivery.Channel);
        Assert.Equal("Echoed: hello", delivery.Payload);
    }

    [Fact]
    public async Task Content_matching_the_scripted_failure_marker_produces_a_direct_failed_outcome_with_no_delivery()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn(ScriptedModelBoundary.FailureMarker), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        var decision = proposal.Direct!;
        // A model that cannot answer is a model failure, and only that kind of result is Failed.
        Assert.Equal(OutcomeCategory.TransientFailure, decision.Category);
        Assert.Equal("scripted_failure", decision.Code);
        Assert.Empty(decision.Deliveries);
    }

    [Theory]
    [InlineData("list stock")]
    [InlineData("  List Stock  ")]
    public async Task List_stock_command_proposes_the_list_stock_tool_call_with_no_untrusted_identity(string content)
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn(content), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.ToolCall, proposal.Kind);
        Assert.Equal("list_stock", proposal.ToolCall!.ToolName);
        Assert.False(proposal.ToolCall.UntrustedArgs.ContainsKey("includeZero"));
    }

    [Fact]
    public async Task List_stock_including_zero_command_proposes_include_zero_true()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("list stock including zero"), BoundConversation, CancellationToken.None);

        Assert.Equal("list_stock", proposal.ToolCall!.ToolName);
        Assert.Equal("true", proposal.ToolCall.UntrustedArgs["includeZero"]);
    }

    [Theory]
    [InlineData("find bolts", "bolts")]
    [InlineData("Find   steel bolts  ", "steel bolts")]
    public async Task Find_command_proposes_the_find_stock_tool_call_with_the_reference_text(string content, string expectedReference)
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn(content), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.ToolCall, proposal.Kind);
        Assert.Equal("find_stock", proposal.ToolCall!.ToolName);
        Assert.Equal(expectedReference, proposal.ToolCall.UntrustedArgs["reference"]);
    }

    // The scripted boundary is deliberately incapable of proposing any tool other than the two
    // recognized read tools - it has no way to express a mutation or an unbounded tool call, matching
    // "invokes list_stock and find_stock only" for this ticket.
    [Fact]
    public async Task Unrecognized_content_never_proposes_a_tool_call()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("delete everything"), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
    }
    // The Foundry conversation the Turn belongs to is trusted context the application establishes and
    // injects; the model boundary genuinely needs it (that is the conversation it continues) and so
    // must refuse to answer without it rather than silently answer outside any conversation.
    [Fact]
    public async Task Planning_without_an_established_foundry_conversation_fails_closed()
    {
        var boundary = new ScriptedModelBoundary();
        var unbound = new ModelInvocationContext(default, Generation: 0, Locale: null);

        var proposal = await boundary.ProposeAsync(Turn("list stock"), unbound, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        Assert.Equal(OutcomeCategory.TransientFailure, proposal.Direct!.Category);
        Assert.Equal("no_conversation_binding", proposal.Direct.Code);
    }
    // The filters and paging bounds a Participant can ask for conversationally. Every value stays
    // untrusted free-form text - the deterministic services resolve and bound it.
    [Fact]
    public async Task List_stock_accepts_bounded_unit_location_and_paging_clauses()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(
            Turn("list stock including zero named bolts unit boxes in Shelf A page size 5 after CURSOR123"),
            BoundConversation,
            CancellationToken.None);

        Assert.Equal("list_stock", proposal.ToolCall!.ToolName);
        Assert.Equal("true", proposal.ToolCall.UntrustedArgs["includeZero"]);
        Assert.Equal("bolts", proposal.ToolCall.UntrustedArgs["nameFilter"]);
        Assert.Equal("boxes", proposal.ToolCall.UntrustedArgs["unit"]);
        Assert.Equal("Shelf A", proposal.ToolCall.UntrustedArgs["location"]);
        Assert.Equal("5", proposal.ToolCall.UntrustedArgs["pageSize"]);
        Assert.Equal("CURSOR123", proposal.ToolCall.UntrustedArgs["cursor"]);
    }

    [Fact]
    public async Task List_stock_can_ask_for_unlocated_stock_explicitly()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("list stock unlocated"), BoundConversation, CancellationToken.None);

        Assert.Equal("list_stock", proposal.ToolCall!.ToolName);
        Assert.Equal("true", proposal.ToolCall.UntrustedArgs["unlocated"]);
        Assert.False(proposal.ToolCall.UntrustedArgs.ContainsKey("location"));
    }

    // A command carrying anything unrecognized is not answered as a narrower request than was asked:
    // it is simply not recognized as a command at all.
    [Fact]
    public async Task List_stock_with_an_unrecognized_clause_is_not_treated_as_a_list_command()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("list stock sorted by price"), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
    }

    [Fact]
    public async Task Find_accepts_unit_and_location_narrowing()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("find steel bolts unit boxes in Shelf A"), BoundConversation, CancellationToken.None);

        Assert.Equal("find_stock", proposal.ToolCall!.ToolName);
        Assert.Equal("steel bolts", proposal.ToolCall.UntrustedArgs["reference"]);
        Assert.Equal("boxes", proposal.ToolCall.UntrustedArgs["unit"]);
        Assert.Equal("Shelf A", proposal.ToolCall.UntrustedArgs["location"]);
    }

    [Fact]
    public async Task Find_can_narrow_to_unlocated_stock()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("find steel bolts unlocated"), BoundConversation, CancellationToken.None);

        Assert.Equal("steel bolts", proposal.ToolCall!.UntrustedArgs["reference"]);
        Assert.Equal("true", proposal.ToolCall.UntrustedArgs["unlocated"]);
    }

    [Fact]
    public async Task Find_keeps_a_reference_that_merely_contains_a_clause_word_intact()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("find bin liners"), BoundConversation, CancellationToken.None);

        Assert.Equal("bin liners", proposal.ToolCall!.UntrustedArgs["reference"]);
    }

    [Theory]
    [InlineData("add stock Steel Bolts quantity 12.5", "add_stock")]
    [InlineData("remove stock Steel Bolts quantity 2", "remove_stock")]
    [InlineData("set stock Steel Bolts quantity 7", "set_stock")]
    public async Task A_mutation_command_proposes_its_bounded_tool_call(string content, string expectedToolName)
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn(content), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.ToolCall, proposal.Kind);
        Assert.Equal(expectedToolName, proposal.ToolCall!.ToolName);
        Assert.Equal("Steel Bolts", proposal.ToolCall.UntrustedArgs["reference"]);
    }

    [Fact]
    public async Task A_mutation_command_carries_its_amount_unit_location_and_note_as_untrusted_text()
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn("add stock Steel Bolts quantity 12.5 unit box in Shelf A note Blue box"),
            BoundConversation,
            CancellationToken.None);

        var args = proposal.ToolCall!.UntrustedArgs;
        Assert.Equal("Steel Bolts", args["reference"]);
        Assert.Equal("12.5", args["quantity"]);
        Assert.Equal("box", args["unit"]);
        Assert.Equal("Shelf A", args["location"]);
        Assert.Equal("Blue box", args["note"]);
    }

    [Fact]
    public async Task A_mutation_command_can_ask_for_stock_kept_nowhere_in_particular()
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn("remove stock Steel Bolts quantity 1 unlocated"), BoundConversation, CancellationToken.None);

        Assert.Equal("true", proposal.ToolCall!.UntrustedArgs["unlocated"]);
    }

    [Fact]
    public async Task A_mutation_command_naming_nothing_to_change_is_not_recognized_as_a_mutation()
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn("add stock"), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        Assert.Equal("echoed", proposal.Direct!.Code);
    }
    // ---- Move, Rename, Forget, batches, confirmation, and rejection (issue #32) ----

    private static async Task<(string ToolName, IReadOnlyDictionary<string, string> Args)> ToolCallAsync(string contentText)
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(Turn(contentText), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.ToolCall, proposal.Kind);
        return (proposal.ToolCall!.ToolName, proposal.ToolCall.UntrustedArgs);
    }

    [Fact]
    public async Task Move_stock_to_a_Location_proposes_a_move_with_that_destination()
    {
        var (toolName, args) = await ToolCallAsync("move stock Steel Bolts quantity 3 to Shelf A");

        Assert.Equal("move_stock", toolName);
        Assert.Equal("Steel Bolts", args["reference"]);
        Assert.Equal("3", args["quantity"]);
        Assert.Equal("Shelf A", args["to"]);
        Assert.False(args.ContainsKey("all"));
    }

    [Fact]
    public async Task Move_stock_all_to_unlocated_proposes_a_move_to_the_unlocated_state()
    {
        var (toolName, args) = await ToolCallAsync("move stock Steel Bolts all to unlocated");

        Assert.Equal("move_stock", toolName);
        Assert.Equal("Steel Bolts", args["reference"]);
        Assert.Equal("true", args["all"]);
        Assert.Equal("true", args["toUnlocated"]);
        Assert.False(args.ContainsKey("to"));
    }

    [Fact]
    public async Task Move_stock_with_an_amount_proposes_a_partial_move()
    {
        var (toolName, args) = await ToolCallAsync("move stock Steel Bolts quantity 2.5 to Shelf B");

        Assert.Equal("move_stock", toolName);
        Assert.Equal("2.5", args["quantity"]);
        Assert.Equal("Shelf B", args["to"]);
    }

    [Fact]
    public async Task Rename_stock_proposes_the_exact_new_name()
    {
        var (toolName, args) = await ToolCallAsync("rename stock Steel Bolts to Brass Rivets");

        Assert.Equal("rename_stock", toolName);
        Assert.Equal("Steel Bolts", args["reference"]);
        Assert.Equal("Brass Rivets", args["newName"]);
        Assert.False(args.ContainsKey("to"));
    }

    [Fact]
    public async Task Forget_stock_proposes_a_forget_for_that_reference()
    {
        var (toolName, args) = await ToolCallAsync("forget stock Steel Bolts unlocated");

        Assert.Equal("forget_stock", toolName);
        Assert.Equal("Steel Bolts", args["reference"]);
        Assert.Equal("true", args["unlocated"]);
    }

    [Fact]
    public async Task Change_stock_proposes_one_batch_carrying_every_sub_command_in_order()
    {
        var (toolName, args) = await ToolCallAsync("change stock: add Bolts quantity 2; forget Rivets");

        Assert.Equal("apply_stock_changes", toolName);
        Assert.True(StockChangeSetParser.TryParse(args["changes"], out var requests, out _));
        Assert.Equal(2, requests.Count);
        Assert.Equal([1, 2], requests.Select(r => r.Order));
        Assert.Equal(StockMutationKind.Add, requests[0].Kind);
        Assert.Equal("Bolts", requests[0].Reference);
        Assert.Equal("2", requests[0].QuantityText);
        Assert.Equal(StockMutationKind.Forget, requests[1].Kind);
        Assert.Equal("Rivets", requests[1].Reference);
    }

    [Fact]
    public async Task Confirm_with_a_code_proposes_the_confirmation_tool_carrying_only_that_code()
    {
        var token = ConfirmationToken.Issue();

        var (toolName, args) = await ToolCallAsync($"confirm {token}");

        Assert.Equal("confirm_inventory_operation", toolName);
        Assert.Equal(token, Assert.Single(args).Value);
        Assert.Equal("token", args.Keys.Single());
    }

    [Fact]
    public async Task Reject_proposes_the_rejection_tool()
    {
        var (toolName, args) = await ToolCallAsync("reject");

        Assert.Equal("reject_inventory_operation", toolName);
        Assert.Empty(args);
    }

    [Fact]
    public async Task A_reference_containing_the_word_to_is_not_split_at_it()
    {
        var (toolName, args) = await ToolCallAsync("forget stock Tomato Paste");

        Assert.Equal("forget_stock", toolName);
        Assert.Equal("Tomato Paste", args["reference"]);
    }

    [Fact]
    public async Task A_change_stock_command_with_an_unrecognized_sub_command_is_not_recognized_at_all()
    {
        var proposal = await new ScriptedModelBoundary().ProposeAsync(
            Turn("change stock: add Bolts quantity 2; destroy Rivets"), BoundConversation, CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        Assert.Equal("echoed", proposal.Direct!.Code);
    }
}
