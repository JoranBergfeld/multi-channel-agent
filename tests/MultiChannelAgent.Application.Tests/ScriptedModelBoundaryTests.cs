using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class ScriptedModelBoundaryTests
{
    [Fact]
    public async Task Ordinary_content_produces_a_completed_outcome_with_one_echo_delivery()
    {
        var boundary = new ScriptedModelBoundary();
        var turn = InboundTurn.Create("native-1", "conversation-1", "hello", null, DateTimeOffset.UtcNow, null);

        var decision = await boundary.DecideAsync(turn, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Completed, decision.Status);
        Assert.Equal("echoed", decision.Code);
        Assert.Equal("Echoed: hello", decision.Summary);
        var delivery = Assert.Single(decision.Deliveries);
        Assert.Equal("synthetic", delivery.Channel);
        Assert.Equal("Echoed: hello", delivery.Payload);
    }

    [Fact]
    public async Task Content_matching_the_scripted_failure_marker_produces_a_failed_outcome_with_no_delivery()
    {
        var boundary = new ScriptedModelBoundary();
        var turn = InboundTurn.Create("native-1", "conversation-1", "trigger-scripted-failure", null, DateTimeOffset.UtcNow, null);

        var decision = await boundary.DecideAsync(turn, CancellationToken.None);

        Assert.Equal(OutcomeStatus.Failed, decision.Status);
        Assert.Equal("scripted_failure", decision.Code);
        Assert.Empty(decision.Deliveries);
    }
}
