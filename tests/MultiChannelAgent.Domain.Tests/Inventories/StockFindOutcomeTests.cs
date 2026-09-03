using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockFindOutcomeTests
{
    private static StockEntrySummary Row(string id) => new(
        new StockEntryId(Guid.Parse(id)),
        "Bolts",
        "bolts",
        new UnitId(Guid.NewGuid()),
        "each",
        null,
        null,
        null,
        Quantity.Create(1m));

    private static List<StockEntrySummary> Rows(int count) =>
        Enumerable.Range(0, count).Select(i => Row($"{i + 1:00000000}-0000-0000-0000-000000000000")).ToList();

    [Fact]
    public void Zero_matches_is_not_found()
    {
        var outcome = StockFindOutcome.FromMatches([]);

        Assert.Equal(StockFindOutcomeKind.NotFound, outcome.Kind);
        Assert.Empty(outcome.Candidates);
        Assert.False(outcome.HasMoreCandidates);
    }

    [Fact]
    public void One_match_is_completed_with_that_single_candidate()
    {
        var only = Row("11111111-0000-0000-0000-000000000000");

        var outcome = StockFindOutcome.FromMatches([only]);

        Assert.Equal(StockFindOutcomeKind.Completed, outcome.Kind);
        Assert.Equal([only], outcome.Candidates);
        Assert.False(outcome.HasMoreCandidates);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Two_to_five_matches_are_ambiguous_with_every_candidate_and_no_more_flag(int count)
    {
        var matches = Rows(count);

        var outcome = StockFindOutcome.FromMatches(matches);

        Assert.Equal(StockFindOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(count, outcome.Candidates.Count);
        Assert.False(outcome.HasMoreCandidates);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(50)]
    public void More_than_five_matches_are_ambiguous_capped_at_five_with_the_more_flag_set(int count)
    {
        var matches = Rows(count);

        var outcome = StockFindOutcome.FromMatches(matches);

        Assert.Equal(StockFindOutcomeKind.Ambiguous, outcome.Kind);
        Assert.Equal(5, outcome.Candidates.Count);
        Assert.True(outcome.HasMoreCandidates);
        Assert.Equal(matches.Take(5), outcome.Candidates);
    }
}
