using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public class OutcomeTests
{
    [Fact]
    public void Completed_outcome_carries_turn_id_code_and_summary()
    {
        var turnId = TurnId.NewId();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var outcome = Outcome.Completed(turnId, code: "echoed", summary: "Echoed: hello", createdAt);

        Assert.Equal(turnId, outcome.TurnId);
        Assert.Equal(OutcomeStatus.Completed, outcome.Status);
        Assert.Equal("echoed", outcome.Code);
        Assert.Equal("Echoed: hello", outcome.Summary);
        Assert.Equal(createdAt, outcome.CreatedAt);
    }

    [Fact]
    public void Failed_outcome_reports_failed_status()
    {
        var turnId = TurnId.NewId();
        var createdAt = DateTimeOffset.UtcNow;

        var outcome = Outcome.Failed(turnId, code: "model_error", summary: "The scripted model rejected the turn.", createdAt);

        Assert.Equal(OutcomeStatus.Failed, outcome.Status);
        Assert.Equal("model_error", outcome.Code);
    }

    [Fact]
    public void Outcome_is_not_terminal_state_holder_for_pending_processing()
    {
        // Only Completed/Failed statuses are constructible via the public factories; there is no
        // "Pending" Outcome because Outcome represents the terminal semantic result of processing.
        var values = Enum.GetValues<OutcomeStatus>();
        Assert.Equal(new[] { OutcomeStatus.Completed, OutcomeStatus.Failed }, values);
    }
}
