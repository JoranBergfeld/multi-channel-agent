namespace MultiChannelAgent.Domain.Inventories;

/// <summary>Semantic shape of a Find resolution, independent of how candidates were matched.</summary>
public enum StockFindOutcomeKind
{
    /// <summary>Exactly one Stock Entry matched.</summary>
    Completed,

    /// <summary>More than one Stock Entry matched; up to five candidates are returned for clarification.</summary>
    Ambiguous,

    /// <summary>No Stock Entry matched.</summary>
    NotFound,
}

/// <summary>
/// The pure interpretation of an already-matched, already-ordered candidate set into Find's semantic
/// result shape: zero matches is <see cref="StockFindOutcomeKind.NotFound"/>; exactly one is
/// <see cref="StockFindOutcomeKind.Completed"/>; more than one is
/// <see cref="StockFindOutcomeKind.Ambiguous"/>, capped at five candidates with
/// <see cref="HasMoreCandidates"/> set when the match set was larger - callers use that flag to offer
/// narrowing guidance rather than ever guessing a match. Never guesses: an ambiguous reference always
/// surfaces candidates instead of silently picking one.
/// </summary>
public sealed record StockFindOutcome
{
    public required StockFindOutcomeKind Kind { get; init; }

    public IReadOnlyList<StockEntrySummary> Candidates { get; init; } = [];

    public bool HasMoreCandidates { get; init; }

    private const int MaxCandidates = 5;

    public static StockFindOutcome FromMatches(IReadOnlyList<StockEntrySummary> orderedMatches)
    {
        if (orderedMatches.Count == 0)
        {
            return new StockFindOutcome { Kind = StockFindOutcomeKind.NotFound };
        }

        if (orderedMatches.Count == 1)
        {
            return new StockFindOutcome { Kind = StockFindOutcomeKind.Completed, Candidates = orderedMatches };
        }

        return new StockFindOutcome
        {
            Kind = StockFindOutcomeKind.Ambiguous,
            Candidates = orderedMatches.Take(MaxCandidates).ToList(),
            HasMoreCandidates = orderedMatches.Count > MaxCandidates,
        };
    }
}
