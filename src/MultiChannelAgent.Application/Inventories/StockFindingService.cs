using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for a Find request.</summary>
public enum StockFindResultKind
{
    Completed,
    Ambiguous,
    NotFound,
    Forbidden,
    Invalid,
}

/// <summary>The candidates a Find request resolved: exactly one when Completed, up to five when Ambiguous.</summary>
public sealed record StockFindView(IReadOnlyList<StockRowView> Candidates, bool HasMoreCandidates);

/// <summary>
/// The semantic result of a Find request: never SQL details, versions, or diagnostics - only a typed
/// <see cref="StockFindResultKind"/>, a machine <see cref="Code"/>, and (only when
/// <see cref="StockFindResultKind.Completed"/> or <see cref="StockFindResultKind.Ambiguous"/>) the
/// resolved candidates.
/// </summary>
public sealed record StockFindResult(StockFindResultKind Kind, StockFindView? View, string Code);

/// <summary>
/// Resolves a Find request for one Inventory: an opaque Stock Entry id reference is matched first;
/// otherwise the reference is matched by normalized name. Never guesses - an ambiguous reference
/// always returns candidates for clarification rather than silently picking one. Authorization always
/// flows through <see cref="InventoryAuthorizationService"/>, exactly as <see cref="StockListingService"/>
/// does, so an unauthorized Inventory is indistinguishable from one that does not exist.
/// </summary>
public sealed class StockFindingService(IStockStore stockStore, InventoryAuthorizationService authorizationService)
{
    private const int MaxCandidates = 5;

    public async Task<StockFindResult> FindAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string? reference,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Viewer, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new StockFindResult(StockFindResultKind.NotFound, null, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new StockFindResult(StockFindResultKind.Forbidden, null, "forbidden");
        }

        StockFindQuery query;
        try
        {
            query = Guid.TryParse(reference, out var stockEntryId)
                ? StockFindQuery.ById(inventoryId, new StockEntryId(stockEntryId))
                : StockFindQuery.ByName(inventoryId, reference, unitId: null, locationId: null);
        }
        catch (ArgumentException)
        {
            return new StockFindResult(StockFindResultKind.Invalid, null, "invalid_reference");
        }

        var matches = await stockStore.FindMatchesAsync(query, MaxCandidates + 1, cancellationToken);
        var outcome = StockFindOutcome.FromMatches(matches);

        return outcome.Kind switch
        {
            StockFindOutcomeKind.NotFound => new StockFindResult(StockFindResultKind.NotFound, null, "not_found"),
            StockFindOutcomeKind.Completed => new StockFindResult(
                StockFindResultKind.Completed,
                new StockFindView(outcome.Candidates.Select(StockListingService.ToRowView).ToList(), false),
                "completed"),
            StockFindOutcomeKind.Ambiguous => new StockFindResult(
                StockFindResultKind.Ambiguous,
                new StockFindView(outcome.Candidates.Select(StockListingService.ToRowView).ToList(), outcome.HasMoreCandidates),
                "ambiguous"),
            _ => throw new InvalidOperationException($"Unhandled {nameof(StockFindOutcomeKind)}: {outcome.Kind}."),
        };
    }
}
