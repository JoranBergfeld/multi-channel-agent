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

    [Fact]
    public void For_part_sequences_follow_the_fixed_range()
    {
        Assert.Equal(100L, TurnEventSequence.ForPart(1));
        Assert.Equal(101L, TurnEventSequence.ForPart(2));
        Assert.Equal(163L, TurnEventSequence.ForPart(64));
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
    public void Every_stream_vocabulary_member_exposes_distinct_non_empty_machine_text()
    {
        AssertDistinctMachineText(Enum.GetValues<TurnEventKind>(), kind => kind.ToMachineText());
        AssertDistinctMachineText(Enum.GetValues<TurnResponsePartKind>(), kind => kind.ToMachineText());
    }

    [Fact]
    public void Stream_vocabulary_ordinals_are_fixed_for_persistence()
    {
        Assert.Equal(0, (int)TurnEventKind.Accepted);
        Assert.Equal(1, (int)TurnEventKind.Processing);
        Assert.Equal(2, (int)TurnEventKind.Part);
        Assert.Equal(3, (int)TurnEventKind.Outcome);

        Assert.Equal(0, (int)TurnResponsePartKind.Text);
        Assert.Equal(1, (int)TurnResponsePartKind.Data);
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

    private static void AssertDistinctMachineText<TEnum>(TEnum[] values, Func<TEnum, string> toMachineText)
        where TEnum : struct, Enum
    {
        var texts = values.Select(toMachineText).ToArray();

        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }
}
