using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryToolRouterTests
{
    private sealed class RecordingDispatcher(string answer) : IToolDispatcher
    {
        public List<string> Dispatched { get; } = [];

        public Task<ModelDecision> DispatchAsync(
            ToolCallProposal proposal, TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Dispatched.Add(proposal.ToolName);

            return Task.FromResult(new ModelDecision
            {
                Category = OutcomeCategory.Completed,
                Code = answer,
                Summary = answer,
            });
        }
    }

    private static TurnExecutionContext Context() => new(
        new TurnId(Guid.NewGuid()),
        new ParticipantId(Guid.NewGuid()),
        new ChannelConversationId("web-conversation-1"),
        new FoundryConversationId(Guid.NewGuid()),
        1,
        new InventoryId(Guid.NewGuid()),
        TraceId: null);

    [Theory]
    [InlineData("list_stock")]
    [InlineData("add_stock")]
    [InlineData("apply_stock_changes")]
    [InlineData("confirm_inventory_operation")]
    [InlineData("reject_inventory_operation")]
    public async Task Every_stock_and_confirmation_tool_reaches_the_stock_dispatcher(string toolName)
    {
        var stock = new RecordingDispatcher("stock");
        var reference = new RecordingDispatcher("reference");

        var decision = await new InventoryToolRouter(stock, reference).DispatchAsync(
            new ToolCallProposal(toolName, new Dictionary<string, string>()),
            Context(),
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal("stock", decision.Code);
        Assert.Equal([toolName], stock.Dispatched);
        Assert.Empty(reference.Dispatched);
    }

    [Theory]
    [InlineData("list_units")]
    [InlineData("create_units")]
    [InlineData("rename_units")]
    [InlineData("add_unit_aliases")]
    [InlineData("remove_unit_aliases")]
    [InlineData("retire_units")]
    [InlineData("list_locations")]
    [InlineData("create_locations")]
    [InlineData("rename_locations")]
    [InlineData("retire_locations")]
    public async Task Every_reference_tool_reaches_the_reference_dispatcher(string toolName)
    {
        var stock = new RecordingDispatcher("stock");
        var reference = new RecordingDispatcher("reference");

        var decision = await new InventoryToolRouter(stock, reference).DispatchAsync(
            new ToolCallProposal(toolName, new Dictionary<string, string>()),
            Context(),
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal("reference", decision.Code);
        Assert.Equal([toolName], reference.Dispatched);
        Assert.Empty(stock.Dispatched);
    }

    [Fact]
    public async Task An_unrecognized_tool_reaches_nobody_and_is_reported_as_a_system_failure()
    {
        var stock = new RecordingDispatcher("stock");
        var reference = new RecordingDispatcher("reference");

        var decision = await new InventoryToolRouter(stock, reference).DispatchAsync(
            new ToolCallProposal("drop_database", new Dictionary<string, string>()),
            Context(),
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

        Assert.Equal(OutcomeCategory.TransientFailure, decision.Category);
        Assert.Equal("unknown_tool", decision.Code);
        Assert.Empty(stock.Dispatched);
        Assert.Empty(reference.Dispatched);
    }
}
