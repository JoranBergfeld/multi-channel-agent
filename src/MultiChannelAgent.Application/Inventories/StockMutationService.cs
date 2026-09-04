using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Semantic outcome shape for one stock mutation.</summary>
public enum StockMutationResultKind
{
    Completed,

    /// <summary>The change is understood and authorized but too consequential to apply unasked (Set to zero).</summary>
    ConfirmationRequired,

    /// <summary>The reference matched several Stock Entries; candidates are offered rather than one being guessed.</summary>
    Ambiguous,

    /// <summary>Nothing matched - or nothing the requester may know exists.</summary>
    NotFound,

    /// <summary>A named Unit or Location does not exist in this Inventory. It is never created implicitly.</summary>
    ReferenceNotFound,

    /// <summary>The requester may see this Inventory but may not mutate it.</summary>
    Forbidden,

    /// <summary>The request conflicts with current Stock (an underflow, or state that changed underneath it).</summary>
    Conflict,

    /// <summary>The request itself could not be understood or was out of bounds.</summary>
    Invalid,
}

/// <summary>
/// One applied mutation as exposed at the application boundary. Both Quantities are exact invariant
/// decimal text - never floating point - so no precision is lost in transit.
/// </summary>
public sealed record StockMutationView(
    string StockEntryId,
    string Name,
    string Unit,
    string? Location,
    string? Note,
    string PreviousQuantity,
    string Quantity,
    bool Created,
    bool NotePreserved);

/// <summary>
/// The semantic result of a mutation request: a typed <see cref="StockMutationResultKind"/>, a machine
/// <see cref="Code"/>, the applied change when there was one, and the candidates when the reference
/// was ambiguous. Never SQL detail, row versions, audit identities, or unauthorized existence.
/// </summary>
public sealed record StockMutationResult(
    StockMutationResultKind Kind,
    StockMutationView? View,
    string Code,
    StockFindView? Candidates = null,
    StockReferenceKind? UnresolvedReference = null);

/// <summary>
/// One mutation request's structured descriptor, as proposed. Every field is untrusted text:
/// <see cref="Reference"/> targets a Stock Entry by opaque identity or exact name,
/// <see cref="QuantityText"/> is invariant decimal text, and the Unit/Location references must resolve
/// exactly. Nothing here is ever pattern-matched or guessed.
/// </summary>
public sealed record StockMutationRequest
{
    public required StockMutationKind Kind { get; init; }

    public string? Reference { get; init; }

    public string? QuantityText { get; init; }

    public string? UnitReference { get; init; }

    public string? LocationReference { get; init; }

    public bool UnlocatedOnly { get; init; }

    public string? Note { get; init; }
}

/// <summary>
/// The deterministic authority for one Add, Remove, or Set. It authorizes (Editor or better), resolves
/// exact Unit/Location references, parses the Quantity, resolves the target through the very same
/// deterministic matching Find uses - so a mutation can never act on a reference Find would have called
/// ambiguous - plans the change with <see cref="StockMutationPlan"/>, and hands one fully decided
/// command to <see cref="IStockMutationStore"/>.
///
/// Callers only ever supply an InventoryId already scoped by trusted context, never one taken from an
/// untrusted model-proposed argument, and an unauthorized Inventory stays indistinguishable from one
/// that does not exist.
/// </summary>
public sealed class StockMutationService(
    IStockStore stockStore,
    IStockMutationStore mutationStore,
    IInventoryReferenceStore referenceStore,
    InventoryAuthorizationService authorizationService)
{
    /// <summary>The reserved Unit every Inventory starts with; an Add that names no Unit creates against it.</summary>
    public const string ReservedEachUnitName = "each";

    public async Task<StockMutationResult> MutateAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        StockOperationId operationId,
        StockMutationRequest request,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new StockMutationResult(StockMutationResultKind.NotFound, null, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new StockMutationResult(StockMutationResultKind.Forbidden, null, "forbidden");
        }

        // Answered from the ledger before anything is resolved or re-planned, because a replay meets
        // Stock its own first attempt already changed. Removing the last 10 and then replaying would
        // otherwise plan "Remove 10 from 0", refuse as an underflow, and tell the Participant nothing
        // happened - the exact opposite of the truth, and unrecoverable once reported. Deliberately
        // after authorization, so a Viewer or a non-member learns nothing from a replay that they
        // could not learn from a first attempt.
        var alreadyRecorded = await mutationStore.FindRecordedAsync(inventoryId, operationId, cancellationToken);
        if (alreadyRecorded is not null)
        {
            return Applied(alreadyRecorded);
        }

        if (!Quantity.TryParseInvariant(request.QuantityText, out var amount))
        {
            return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_quantity");
        }

        var proposedNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (proposedNote is { Length: > StockEntry.MaxNoteLength })
        {
            return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_note");
        }

        UnitId? unitId = null;
        if (!string.IsNullOrWhiteSpace(request.UnitReference))
        {
            unitId = await referenceStore.ResolveUnitAsync(inventoryId, request.UnitReference, cancellationToken);
            if (unitId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Unit);
            }
        }

        LocationId? locationId = null;
        if (!string.IsNullOrWhiteSpace(request.LocationReference))
        {
            locationId = await referenceStore.ResolveLocationAsync(inventoryId, request.LocationReference, cancellationToken);
            if (locationId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Location);
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
            return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_reference");
        }

        var matches = await stockStore.FindMatchesAsync(query, StockFindingService.MaxCandidates + 1, cancellationToken);
        var resolution = StockFindOutcome.FromMatches(matches);

        // A mutation acts on one Match. Several matched, so the request names no single Stock Entry -
        // choosing one would be exactly the guess CONTEXT.md forbids.
        if (resolution.Kind == StockFindOutcomeKind.Ambiguous)
        {
            var facets = await stockStore.SummarizeMatchFacetsAsync(query, StockFindingService.MaxCandidates, cancellationToken);

            return new StockMutationResult(
                StockMutationResultKind.Ambiguous,
                null,
                "ambiguous",
                new StockFindView(
                    resolution.Candidates.Select(StockListingService.ToRowView).ToList(),
                    resolution.HasMoreCandidates,
                    StockNarrowingHints.FromFacets(facets)));
        }

        var target = resolution.Kind == StockFindOutcomeKind.Completed ? resolution.Candidates[0] : null;
        var plan = StockMutationPlan.For(request.Kind, target?.Quantity, amount);

        switch (plan.Kind)
        {
            case StockMutationPlanKind.InvalidAmount:
                return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_quantity");

            case StockMutationPlanKind.OutOfBounds:
                return new StockMutationResult(StockMutationResultKind.Invalid, null, "quantity_out_of_bounds");

            case StockMutationPlanKind.Underflow:
                return new StockMutationResult(StockMutationResultKind.Conflict, null, "insufficient_quantity");

            case StockMutationPlanKind.TargetRequired:
                return new StockMutationResult(StockMutationResultKind.NotFound, null, "not_found");

            case StockMutationPlanKind.ConfirmationRequired:
                return new StockMutationResult(StockMutationResultKind.ConfirmationRequired, null, "confirmation_required");
        }

        var creating = plan.Kind == StockMutationPlanKind.CreateEntry;
        string? newEntryName = null;
        UnitId? newEntryUnitId = null;

        if (creating)
        {
            // An opaque identity that matched nothing names no Stock Entry to create - it names one
            // that is simply not here.
            if (query.NormalizedNameReference is null)
            {
                return new StockMutationResult(StockMutationResultKind.NotFound, null, "not_found");
            }

            newEntryName = request.Reference?.Trim();
            if (newEntryName is null or { Length: 0 } or { Length: > StockEntry.MaxNameLength })
            {
                return new StockMutationResult(StockMutationResultKind.Invalid, null, "invalid_name");
            }

            // A blank Unit means the reserved `each` every Inventory starts with - never a Unit
            // invented on the Participant's behalf.
            newEntryUnitId = unitId ?? await referenceStore.ResolveUnitAsync(inventoryId, ReservedEachUnitName, cancellationToken);
            if (newEntryUnitId is null)
            {
                return ReferenceNotFound(StockReferenceKind.Unit);
            }
        }

        var stored = await mutationStore.ApplyAsync(
            new StockMutationCommand
            {
                OperationId = operationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                Kind = request.Kind,
                Amount = amount,
                ResultingQuantity = plan.ResultingQuantity,
                StockEntryId = target?.Id,
                ExpectedQuantity = target?.Quantity,
                NewEntryName = newEntryName,
                NewEntryUnitId = newEntryUnitId,
                NewEntryLocationId = creating ? locationId : null,
                Note = creating ? proposedNote : null,

                // An existing Stock Entry's Note is never rewritten by a quantity change, so a proposed
                // Note is deliberately left unapplied - and said out loud rather than dropped silently.
                NotePreserved = proposedNote is not null && !creating,
                Now = now,
            },
            cancellationToken);

        if (stored.Outcome == StockMutationStoreOutcome.StateChanged)
        {
            return new StockMutationResult(StockMutationResultKind.Conflict, null, "state_changed");
        }

        return Applied(stored.Recorded!);
    }

    /// <summary>
    /// The one place an applied effect becomes an answer, so a replay served from the ledger, a store
    /// that converged on an already-applied operation, and a first attempt that has just written are
    /// literally indistinguishable to a Participant.
    /// </summary>
    private static StockMutationResult Applied(RecordedStockMutation recorded) =>
        new(
            StockMutationResultKind.Completed,
            new StockMutationView(
                recorded.StockEntryId.ToString(),
                recorded.Name,
                recorded.UnitCanonicalName,
                recorded.LocationName,
                recorded.Note,
                recorded.PreviousQuantity.ToInvariantText(),
                recorded.ResultingQuantity.ToInvariantText(),
                recorded.CreatedEntry,
                recorded.NotePreserved),
            "completed");

    private static StockMutationResult ReferenceNotFound(StockReferenceKind reference) =>
        new(StockMutationResultKind.ReferenceNotFound, null, "reference_not_found", null, reference);
}
