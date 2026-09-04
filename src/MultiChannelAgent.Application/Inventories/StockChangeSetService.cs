using System.Globalization;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

// System.Globalization is used for exactly one thing here: rendering a proposal's expiry as
// culture-invariant round-trip text, so a client in any locale reads the same instant.

/// <summary>Semantic outcome shape for one change set.</summary>
public enum StockChangeSetResultKind
{
    Completed,

    /// <summary>The changes are understood and authorized but too consequential to apply unasked; an exact proposal is stored.</summary>
    ConfirmationRequired,

    Ambiguous,
    NotFound,
    ReferenceNotFound,
    Forbidden,
    Conflict,
    Invalid,
}

/// <summary>One Stock Entry's before-and-after, as exposed at the application boundary. Quantities are exact invariant decimal text.</summary>
public sealed record StockEntryStateView(
    string? StockEntryId,
    string Name,
    string Unit,
    string? Location,
    string? Note,
    string PreviousQuantity,
    string Quantity,
    bool Retired);

/// <summary>
/// One change, exactly as proposed or exactly as applied. <see cref="SurvivingStockEntryId"/> and
/// <see cref="RetiredStockEntryId"/> are the answer every merge-retiring Move and Rename owes the
/// Participant.
/// </summary>
public sealed record StockChangeView(
    int Order,
    string Operation,
    string Effect,
    StockEntryStateView Source,
    StockEntryStateView? Destination,
    string TransferredQuantity,
    string? NewName,
    string? SurvivingStockEntryId,
    string? RetiredStockEntryId);

/// <summary>What one applied change set did.</summary>
public sealed record StockChangeSetView(IReadOnlyList<StockChangeView> Changes);

/// <summary>
/// An exact stored proposal, as shown to the Participant. <see cref="Token"/> is the plaintext
/// confirmation code: the proposal itself keeps only its hash, while this view - and the Outcome
/// payload and Delivery built from it - carry the code the Participant has to quote back. See
/// <see cref="ConfirmationToken"/> for exactly where it lives and for how long.
/// </summary>
public sealed record StockProposalView(string Token, string ExpiresAt, IReadOnlyList<StockChangeView> Changes);

/// <summary>The semantic result of a change-set request. Never SQL detail, row versions, audit identities, or unauthorized existence.</summary>
public sealed record StockChangeSetResult(
    StockChangeSetResultKind Kind,
    string Code,
    StockChangeSetView? Applied = null,
    StockProposalView? Proposal = null,
    StockFindView? Candidates = null,
    StockReferenceKind? UnresolvedReference = null);

/// <summary>
/// The deterministic authority for one set of stock changes: authorize, answer a replay, resolve
/// every change against current state, and then either apply one low-risk change immediately or
/// store an exact proposal and hand back its one-time token.
///
/// The confirmation rule lives in one expression - more than one change, or any change whose effect
/// requires it - so a batch, a Set to zero, a Forget, and a merge-retiring Move or Rename all take
/// the same path for the same stated reason.
///
/// Callers only ever supply an InventoryId already scoped by trusted context, and an unauthorized
/// Inventory stays indistinguishable from one that does not exist.
/// </summary>
public sealed class StockChangeSetService(
    StockChangeResolver resolver,
    IStockChangeSetStore changeSetStore,
    IConfirmationProposalStore proposalStore,
    InventoryAuthorizationService authorizationService)
{
    public async Task<StockChangeSetResult> ApplyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        StockOperationId operationId,
        IReadOnlyList<StockChangeRequest> requests,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, MembershipRole.Editor, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.NotFound, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.Forbidden, "forbidden");
        }

        // Answered from the ledger before anything is resolved or re-planned, because a replayed Turn
        // meets Stock its own first attempt already changed. Re-planning first would see the entry it
        // merged away as missing and report "not found", telling the Participant nothing happened
        // after everything had. Deliberately after authorization, so a Viewer or a non-member learns
        // nothing from a replay they could not learn from a first attempt.
        if (await changeSetStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyRecorded)
        {
            return Applied(alreadyRecorded);
        }

        if (requests.Count == 0)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.Invalid, "invalid_changes");
        }

        if (requests.Count > ConfirmationProposal.MaxChanges)
        {
            return new StockChangeSetResult(StockChangeSetResultKind.Invalid, "too_many_changes");
        }

        var changes = new List<ProposedChange>(requests.Count);
        var versions = new Dictionary<StockEntryId, ExpectedEntryVersion>();
        var absences = new List<ExpectedEquivalentStockAbsence>();
        var touched = new HashSet<StockEntryId>();
        var claimedKeys = new HashSet<ExpectedEquivalentStockAbsence>();

        foreach (var request in requests.OrderBy(request => request.Order))
        {
            var resolution = await resolver.ResolveAsync(inventoryId, request, cancellationToken);
            if (resolution.Kind != StockChangeResolutionKind.Resolved)
            {
                // One refusal refuses the whole set. A batch is atomic, so answering "these three
                // worked and that one did not" would be describing a state that never exists.
                return Refused(resolution);
            }

            var change = resolution.Change!;

            // Every change in a set is resolved against the state the set started from, so two
            // changes to one Stock Entry would each be planned as if the other had not happened.
            // Refusing is the only answer that cannot silently apply arithmetic nobody asked for.
            // A relocation names its own row as both source and destination, so a change's own
            // identities are collapsed first and only overlap *between* changes is a conflict.
            var touchedByThisChange = new[] { change.Source.StockEntryId, change.Destination?.StockEntryId }
                .OfType<StockEntryId>()
                .ToHashSet();

            if (touchedByThisChange.Overlaps(touched))
            {
                return new StockChangeSetResult(StockChangeSetResultKind.Invalid, "conflicting_changes");
            }

            // Two changes can also collide without sharing a Stock Entry, by both landing on one
            // Equivalent Stock key - two creates of the same name, or two Renames into it. Each was
            // resolved against a state in which that key was free, so left to execution they would
            // violate the uniqueness index halfway through a transaction. Refusing here answers the
            // Participant plainly instead.
            if (resolution.ExpectedAbsence is { } claimed && !claimedKeys.Add(claimed))
            {
                return new StockChangeSetResult(StockChangeSetResultKind.Invalid, "conflicting_changes");
            }

            touched.UnionWith(touchedByThisChange);

            changes.Add(change);

            foreach (var version in resolution.ExpectedVersions ?? [])
            {
                versions[version.StockEntryId] = version;
            }

            if (resolution.ExpectedAbsence is { } absence)
            {
                absences.Add(absence);
            }
        }

        var requiresConfirmation = changes.Count > 1 || changes.Any(change => StockAuditFacts.RequiresConfirmation(change.Effect));

        return requiresConfirmation
            ? await ProposeAsync(
                participantId, inventoryId, turnId, channelConversationId, changes, versions.Values.ToList(), absences, now, cancellationToken)
            : await ApplyImmediatelyAsync(
                participantId, inventoryId, turnId, operationId, changes, versions.Values.ToList(), absences, now, cancellationToken);
    }

    private async Task<StockChangeSetResult> ProposeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string channelConversationId,
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> versions,
        IReadOnlyList<ExpectedEquivalentStockAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Issued here, hashed into the proposal, and returned in the answer. The proposal row itself
        // never holds anything that could approve it; the answer necessarily does, because the
        // Participant has to be told the code and has to be able to reconnect to it.
        var token = ConfirmationToken.Issue();

        var proposal = ConfirmationProposal.Create(
            ConfirmationToken.HashOf(token),
            participantId,
            channelConversationId,
            inventoryId,
            turnId,
            changes,
            versions,
            absences,
            now);

        // Storing supersedes whatever was pending in this conversation, atomically, so a confirmation
        // arriving now can only ever mean this proposal.
        await proposalStore.StoreAsync(proposal, now, cancellationToken);

        return new StockChangeSetResult(
            StockChangeSetResultKind.ConfirmationRequired,
            "confirmation_required",
            Proposal: new StockProposalView(
                token,
                proposal.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                proposal.Changes.Select(change => ToChangeView(change)).ToList()));
    }

    private async Task<StockChangeSetResult> ApplyImmediatelyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        StockOperationId operationId,
        IReadOnlyList<ProposedChange> changes,
        IReadOnlyList<ExpectedEntryVersion> versions,
        IReadOnlyList<ExpectedEquivalentStockAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = await changeSetStore.ApplyAsync(
            new StockChangeSetCommand
            {
                OperationId = operationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConfirmedByTurnId = turnId,
                ConsumesProposalId = null,
                Changes = changes,
                ExpectedVersions = versions,
                ExpectedAbsences = absences,
                Now = now,
            },
            cancellationToken);

        return stored.Outcome == StockChangeSetStoreOutcome.Conflict
            ? new StockChangeSetResult(StockChangeSetResultKind.Conflict, "state_changed")
            : Applied(stored.Recorded!);
    }

    /// <summary>
    /// The one place an applied change set becomes an answer, so a replay served from the ledger, a
    /// store that converged on an already-applied operation, and a first attempt that has just
    /// written are literally indistinguishable to a Participant.
    /// </summary>
    internal static StockChangeSetResult Applied(RecordedStockChangeSet recorded) => new(
        StockChangeSetResultKind.Completed,
        "completed",
        new StockChangeSetView(recorded.Effects.Select(effect => ToChangeView(effect)).ToList()));

    internal static StockChangeView ToChangeView(RecordedStockChangeEffect effect) => new(
        effect.Order,
        StockMutationKinds.ToMachineText(effect.Kind),
        EffectText(effect.Effect),
        ToStateView(effect.Source),
        effect.Destination is null ? null : ToStateView(effect.Destination),
        effect.TransferredQuantity.ToInvariantText(),
        effect.NewName,
        effect.SurvivingStockEntryId?.ToString(),
        effect.RetiredStockEntryId?.ToString());

    internal static StockChangeView ToChangeView(ProposedChange change) => new(
        change.Order,
        StockMutationKinds.ToMachineText(change.Kind),
        EffectText(change.Effect),
        ToStateView(change.Source),
        change.Destination is null ? null : ToStateView(change.Destination),
        change.TransferredQuantity.ToInvariantText(),
        change.NewName,
        change.SurvivingStockEntryId?.ToString(),
        change.RetiredStockEntryId?.ToString());

    /// <summary>The stable machine text for an effect, in the same lower_snake shape every other code uses.</summary>
    internal static string EffectText(StockChangeEffectKind effect) => effect switch
    {
        StockChangeEffectKind.Created => "created",
        StockChangeEffectKind.QuantityIncreased => "quantity_increased",
        StockChangeEffectKind.QuantityDecreased => "quantity_decreased",
        StockChangeEffectKind.QuantitySet => "quantity_set",
        StockChangeEffectKind.QuantityCleared => "quantity_cleared",
        StockChangeEffectKind.Placed => "placed",
        StockChangeEffectKind.Split => "split",
        StockChangeEffectKind.SplitMerged => "split_merged",
        StockChangeEffectKind.Merged => "merged",
        StockChangeEffectKind.Renamed => "renamed",
        StockChangeEffectKind.RenameMerged => "rename_merged",
        StockChangeEffectKind.Forgotten => "forgotten",
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "Unhandled stock change effect."),
    };

    private static StockEntryStateView ToStateView(ProposedEntryState state) => new(
        state.StockEntryId?.ToString(),
        state.Name,
        state.UnitCanonicalName,
        state.LocationName,
        state.Note,
        state.PreviousQuantity.ToInvariantText(),
        state.ResultingQuantity.ToInvariantText(),
        state.Retired);

    private static StockEntryStateView ToStateView(RecordedEntryState state) => new(
        state.StockEntryId.ToString(),
        state.Name,
        state.UnitCanonicalName,
        state.LocationName,
        Note: null,
        state.PreviousQuantity.ToInvariantText(),
        state.ResultingQuantity.ToInvariantText(),
        state.Retired);

    private static StockChangeSetResult Refused(StockChangeResolution resolution) => resolution.Kind switch
    {
        StockChangeResolutionKind.Ambiguous => new StockChangeSetResult(
            StockChangeSetResultKind.Ambiguous, resolution.Code, Candidates: resolution.Candidates),
        StockChangeResolutionKind.NotFound => new StockChangeSetResult(StockChangeSetResultKind.NotFound, resolution.Code),
        StockChangeResolutionKind.ReferenceNotFound => new StockChangeSetResult(
            StockChangeSetResultKind.ReferenceNotFound, resolution.Code, UnresolvedReference: resolution.UnresolvedReference),
        StockChangeResolutionKind.Conflict => new StockChangeSetResult(StockChangeSetResultKind.Conflict, resolution.Code),
        _ => new StockChangeSetResult(StockChangeSetResultKind.Invalid, resolution.Code),
    };
}
