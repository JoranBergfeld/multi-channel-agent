using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Turns;

public class TurnStreamEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Event_sequences_strictly_increase()
    {
        Assert.True(TurnEventSequence.Accepted < TurnEventSequence.Processing);
        Assert.True(TurnEventSequence.Processing < TurnEventSequence.ForPart(1));
        Assert.True(TurnEventSequence.ForPart(1) < TurnEventSequence.ForPart(TurnEventSequence.MaxParts));
        Assert.True(TurnEventSequence.ForPart(TurnEventSequence.MaxParts) < TurnEventSequence.Outcome);
    }

    [Theory]
    [InlineData(1, 100L)]
    [InlineData(2, 101L)]
    [InlineData(64, 163L)]
    public void For_part_sequences_follow_the_fixed_range(int order, long expectedSequence)
    {
        Assert.Equal(expectedSequence, TurnEventSequence.ForPart(order));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void For_part_rejects_orders_outside_the_fixed_range(int order)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TurnEventSequence.ForPart(order));
    }

    [Fact]
    public void Sequence_issue_checks_cover_only_the_fixed_vocabulary()
    {
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.Accepted));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.Processing));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.ForPart(1)));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.ForPart(TurnEventSequence.MaxParts)));
        Assert.True(TurnEventSequence.IsIssued(TurnEventSequence.Outcome));

        Assert.False(TurnEventSequence.IsIssued(0));
        Assert.False(TurnEventSequence.IsIssued(3));
        Assert.False(TurnEventSequence.IsIssued(TurnEventSequence.ForPart(TurnEventSequence.MaxParts) + 1));
        Assert.False(TurnEventSequence.IsIssued(TurnEventSequence.Outcome + 1));
        Assert.False(TurnEventSequence.IsIssued(-1));
    }

    [Fact]
    public void Machine_text_is_stable_for_event_and_part_kinds()
    {
        Assert.Equal("accepted", TurnEventKind.Accepted.ToMachineText());
        Assert.Equal("processing", TurnEventKind.Processing.ToMachineText());
        Assert.Equal("part", TurnEventKind.Part.ToMachineText());
        Assert.Equal("outcome", TurnEventKind.Outcome.ToMachineText());

        Assert.Equal("text", TurnResponsePartKind.Text.ToMachineText());
        Assert.Equal("data", TurnResponsePartKind.Data.ToMachineText());
    }

    [Fact]
    public void Processing_progress_event_carries_the_fixed_sequence_and_expiry()
    {
        var turnId = TurnId.NewId();

        var progress = TurnProgressEvent.Processing(turnId, Now);

        Assert.Equal(turnId, progress.TurnId);
        Assert.Equal(TurnEventSequence.Processing, progress.Sequence);
        Assert.Equal(TurnEventKind.Processing, progress.Kind);
        Assert.Equal(Now, progress.OccurredAt);
        Assert.Equal(Now + TurnProgressEvent.Retention, progress.ExpiresAt);
    }

    [Fact]
    public void Progress_event_retention_matches_outcome_payload_retention()
    {
        Assert.Equal(Outcome.PayloadRetention, TurnProgressEvent.Retention);
    }
}
