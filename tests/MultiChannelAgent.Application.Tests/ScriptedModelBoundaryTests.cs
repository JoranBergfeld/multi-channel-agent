using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class ScriptedModelBoundaryTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static InboundTurn Turn(string contentText) =>
        InboundTurn.Create("native-1", SomeParticipant, "conversation-1", contentText, null, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task Ordinary_unrecognized_content_produces_a_direct_completed_outcome_with_one_echo_delivery()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("hello"), CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        var decision = proposal.Direct!;
        Assert.Equal(OutcomeStatus.Completed, decision.Status);
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

        var proposal = await boundary.ProposeAsync(Turn(ScriptedModelBoundary.FailureMarker), CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
        var decision = proposal.Direct!;
        Assert.Equal(OutcomeStatus.Failed, decision.Status);
        Assert.Equal("scripted_failure", decision.Code);
        Assert.Empty(decision.Deliveries);
    }

    [Theory]
    [InlineData("list stock")]
    [InlineData("  List Stock  ")]
    public async Task List_stock_command_proposes_the_list_stock_tool_call_with_no_untrusted_identity(string content)
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn(content), CancellationToken.None);

        Assert.Equal(ModelProposalKind.ToolCall, proposal.Kind);
        Assert.Equal("list_stock", proposal.ToolCall!.ToolName);
        Assert.False(proposal.ToolCall.UntrustedArgs.ContainsKey("includeZero"));
    }

    [Fact]
    public async Task List_stock_including_zero_command_proposes_include_zero_true()
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn("list stock including zero"), CancellationToken.None);

        Assert.Equal("list_stock", proposal.ToolCall!.ToolName);
        Assert.Equal("true", proposal.ToolCall.UntrustedArgs["includeZero"]);
    }

    [Theory]
    [InlineData("find bolts", "bolts")]
    [InlineData("Find   steel bolts  ", "steel bolts")]
    public async Task Find_command_proposes_the_find_stock_tool_call_with_the_reference_text(string content, string expectedReference)
    {
        var boundary = new ScriptedModelBoundary();

        var proposal = await boundary.ProposeAsync(Turn(content), CancellationToken.None);

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

        var proposal = await boundary.ProposeAsync(Turn("delete everything"), CancellationToken.None);

        Assert.Equal(ModelProposalKind.Direct, proposal.Kind);
    }
}
