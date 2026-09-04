using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for a Find request.</summary>
public enum StockFindResultKind
{
    Completed,
    Ambiguous,
    NotFound,

    /// <summary>A named Unit or Location used to narrow the reference does not exist in this Inventory.</summary>
    ReferenceNotFound,
    Forbidden,
    Invalid,
}

/// <summary>
/// What a Participant can narrow an ambiguous reference by, drawn from the whole match set rather
/// than only the candidates shown: the Units and Locations those matches actually occupy, and whether
/// any of them is kept nowhere in particular. Each list is only populated when the matches genuinely
/// differ on it, so a hint is never advice that would change nothing.
/// </summary>
public sealed record StockNarrowingHints(
    IReadOnlyList<string> Units,
    IReadOnlyList<string> Locations,
    bool IncludesUnlocated)
{
    public static readonly StockNarrowingHints None = new([], [], false);

    public bool HasAny => Units.Count > 0 || Locations.Count > 0 || IncludesUnlocated;

    /// <summary>
    /// Offers only narrowing that would actually change the answer: a Unit list when the matches
    /// differ on it, a Location list when placement genuinely distinguishes them, and unlocated Stock
    /// only when some match really is kept nowhere in particular alongside placed ones. Shared by
    /// Find and by an ambiguous mutation so both offer a Participant exactly the same choices.
    /// </summary>
    public static StockNarrowingHints FromFacets(StockMatchFacets facets)
    {
        var units = facets.UnitCanonicalNames.Count > 1 ? facets.UnitCanonicalNames : [];
        var distinguishesByPlacement = facets.LocationNames.Count > 1
            || (facets.LocationNames.Count == 1 && facets.HasUnlocatedMatches);

        return new StockNarrowingHints(
            units,
            distinguishesByPlacement ? facets.LocationNames : [],
            distinguishesByPlacement && facets.HasUnlocatedMatches);
    }
}

/// <summary>
/// The candidates a Find request resolved: exactly one when Completed, up to five when Ambiguous,
/// plus how to narrow when more matched than could be shown.
/// </summary>
public sealed record StockFindView(
    IReadOnlyList<StockRowView> Candidates,
    bool HasMoreCandidates,
    StockNarrowingHints NarrowingHints);

/// <summary>
/// The semantic result of a Find request: never SQL details, versions, or diagnostics - only a typed
/// <see cref="StockFindResultKind"/>, a machine <see cref="Code"/>, and (only when
/// <see cref="StockFindResultKind.Completed"/> or <see cref="StockFindResultKind.Ambiguous"/>) the
/// resolved candidates.
/// </summary>
public sealed record StockFindResult(
    StockFindResultKind Kind, StockFindView? View, string Code, StockReferenceKind? UnresolvedReference = null);

/// <summary>
/// One Find request's structured descriptor. <see cref="Reference"/> targets a Stock Entry either by
/// its opaque identity or by an exact name; <see cref="UnitReference"/>, <see cref="LocationReference"/>,
/// and <see cref="UnlocatedOnly"/> narrow that reference to an exact Inventory-owned Unit or Location
/// (or to Stock kept nowhere in particular). Nothing here is ever pattern-matched or guessed.
/// </summary>
public sealed record StockFindRequest
{
    public string? Reference { get; init; }

    public string? UnitReference { get; init; }

    public string? LocationReference { get; init; }

    public bool UnlocatedOnly { get; init; }
}

/// <summary>
/// Resolves a Find request for one Inventory: an opaque Stock Entry id reference is matched first;
/// otherwise the reference is matched by normalized name, narrowed by any exact Unit/Location the
/// request names. Never guesses - an ambiguous reference always returns candidates and actionable
/// narrowing for clarification rather than silently picking one. Authorization always flows through
/// <see cref="InventoryAuthorizationService"/>, exactly as <see cref="StockListingService"/> does, so
/// an unauthorized Inventory is indistinguishable from one that does not exist.
/// </summary>
public sealed class StockFindingService(
    IStockStore stockStore,
    IInventoryReferenceStore referenceStore,
    InventoryAuthorizationService authorizationService)
{
    /// <summary>The most candidates an ambiguous answer ever shows; beyond this it offers narrowing instead.</summary>
    public const int MaxCandidates = 5;

    public async Task<StockFindResult> FindAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        StockFindRequest request,
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

        UnitId? unitId = null;
        if (!string.IsNullOrWhiteSpace(request.UnitReference))
        {
            unitId = await referenceStore.ResolveUnitAsync(inventoryId, request.UnitReference, cancellationToken);
            if (unitId is null)
            {
                return new StockFindResult(StockFindResultKind.ReferenceNotFound, null, "reference_not_found", StockReferenceKind.Unit);
            }
        }

        LocationId? locationId = null;
        if (!string.IsNullOrWhiteSpace(request.LocationReference))
        {
            locationId = await referenceStore.ResolveLocationAsync(inventoryId, request.LocationReference, cancellationToken);
            if (locationId is null)
            {
                return new StockFindResult(StockFindResultKind.ReferenceNotFound, null, "reference_not_found", StockReferenceKind.Location);
            }
        }

        StockFindQuery query;
        try
        {
            query = Guid.TryParse(request.Reference, out var stockEntryId)
                ? StockFindQuery.ById(inventoryId, new StockEntryId(stockEntryId))
                : StockFindQuery.ByName(inventoryId, request.Reference, unitId, locationId, request.UnlocatedOnly);
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
                new StockFindView(outcome.Candidates.Select(StockListingService.ToRowView).ToList(), false, StockNarrowingHints.None),
                "completed"),
            StockFindOutcomeKind.Ambiguous => new StockFindResult(
                StockFindResultKind.Ambiguous,
                new StockFindView(
                    outcome.Candidates.Select(StockListingService.ToRowView).ToList(),
                    outcome.HasMoreCandidates,
                    await NarrowingHintsAsync(query, cancellationToken)),
                "ambiguous"),
            _ => throw new InvalidOperationException($"Unhandled {nameof(StockFindOutcomeKind)}: {outcome.Kind}."),
        };
    }

    private async Task<StockNarrowingHints> NarrowingHintsAsync(StockFindQuery query, CancellationToken cancellationToken) =>
        StockNarrowingHints.FromFacets(await stockStore.SummarizeMatchFacetsAsync(query, MaxCandidates, cancellationToken));
}
