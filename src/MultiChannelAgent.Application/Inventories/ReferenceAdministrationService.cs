using System.Globalization;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

// System.Globalization is used for exactly one thing here: rendering a proposal's expiry as
// culture-invariant round-trip text, so a client in any locale reads the same instant.

/// <summary>Semantic outcome shape for one reference administration change set.</summary>
public enum ReferenceAdministrationResultKind
{
    Completed,

    /// <summary>The changes are understood and authorized but too consequential to apply unasked; an exact proposal is stored.</summary>
    ConfirmationRequired,

    /// <summary>No accessible Inventory - identical whether it does not exist or is simply not authorized.</summary>
    NotFound,

    /// <summary>A named Unit or Location does not exist here, or is retired. Bounded deterministic suggestions accompany it.</summary>
    ReferenceNotFound,

    Forbidden,
    Conflict,
    Invalid,
}

/// <summary>
/// One administration change, exactly as proposed or exactly as applied. Every field is a semantic
/// fact: no versions, no audit identities, no SQL detail.
/// </summary>
public sealed record ReferenceChangeView(
    int Order,
    string Operation,
    string Reference,
    string ReferenceId,
    string Name,
    string? NewName,
    string? Alias,
    IReadOnlyList<string> Aliases);

/// <summary>What one applied administration change set did.</summary>
public sealed record ReferenceChangeSetView(IReadOnlyList<ReferenceChangeView> Changes);

/// <summary>
/// An exact stored administration proposal, as shown to the Participant. <see cref="Token"/> is the
/// plaintext confirmation code; the proposal itself keeps only its hash. See
/// <see cref="ConfirmationToken"/> for exactly where it lives and for how long.
/// </summary>
public sealed record ReferenceProposalView(string Token, string ExpiresAt, IReadOnlyList<ReferenceChangeView> Changes);

/// <summary>The semantic result of an administration request. Never SQL detail, row versions, audit identities, or unauthorized existence.</summary>
public sealed record ReferenceAdministrationResult(
    ReferenceAdministrationResultKind Kind,
    string Code,
    ReferenceChangeSetView? Applied = null,
    ReferenceProposalView? Proposal = null,
    ReferenceKind? UnresolvedReference = null,
    IReadOnlyList<string>? Suggestions = null);

/// <summary>
/// The deterministic authority for one set of Unit and Location changes: authorize the role the
/// changes actually demand, answer a replay, resolve every change against current state, and then
/// either apply one non-destructive change immediately or store an exact proposal and hand back its
/// one-time token.
///
/// Three rules live here and nowhere else, each in one expression:
/// <list type="bullet">
/// <item>the required role is Owner when any change retires, and Editor otherwise;</item>
/// <item>confirmation is required when there is more than one change, or any change retires;</item>
/// <item>a set refuses whole - one refusal, one reference touched twice, or one term claimed twice.</item>
/// </list>
///
/// Callers only ever supply an InventoryId already scoped by trusted context, and an unauthorized
/// Inventory stays indistinguishable from one that does not exist.
/// </summary>
public sealed class ReferenceAdministrationService(
    ReferenceChangeResolver resolver,
    IReferenceAdministrationStore administrationStore,
    IConfirmationProposalStore proposalStore,
    InventoryAuthorizationService authorizationService)
{
    public async Task<ReferenceAdministrationResult> ApplyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        ReferenceOperationId operationId,
        IReadOnlyList<ReferenceChangeRequest> requests,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        // The role the *requested* changes demand, decided before anything is resolved, so an Editor
        // asking to Retire is refused without ever learning whether the target exists.
        var requiredRole = requests.Any(request => ReferenceAdministrationFacts.RequiredRole(request.Kind) == MembershipRole.Owner)
            ? MembershipRole.Owner
            : MembershipRole.Editor;

        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, requiredRole, channelConversationId, now, cancellationToken);

        if (authorization.Outcome == InventoryAuthorizationOutcome.NotFound)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.NotFound, "not_found");
        }

        if (authorization.Outcome == InventoryAuthorizationOutcome.Forbidden)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Forbidden, "forbidden");
        }

        // Answered from the ledger before anything is resolved or re-planned, because a replayed Turn
        // meets a catalog its own first attempt already changed - re-planning would report the Unit it
        // created as a collision. Deliberately after authorization, so a Viewer or a non-member learns
        // nothing from a replay they could not learn from a first attempt.
        if (await administrationStore.FindRecordedByTurnAsync(inventoryId, turnId, cancellationToken) is { } alreadyRecorded)
        {
            return Applied(alreadyRecorded);
        }

        if (requests.Count == 0)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Invalid, "invalid_changes");
        }

        if (requests.Count > ConfirmationProposal.MaxChanges)
        {
            return new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Invalid, "too_many_changes");
        }

        var changes = new List<ProposedReferenceChange>(requests.Count);
        var versions = new Dictionary<(ReferenceKind, Guid), ExpectedReferenceVersion>();
        var absences = new List<ExpectedTermAbsence>();
        var touched = new HashSet<(ReferenceKind, Guid)>();
        var claimedTerms = new HashSet<ExpectedTermAbsence>();

        foreach (var request in requests.OrderBy(request => request.Order))
        {
            var resolution = await resolver.ResolveAsync(inventoryId, request, cancellationToken);
            if (resolution.Kind != ReferenceChangeResolutionKind.Resolved)
            {
                // One refusal refuses the whole set. A batch is atomic, so answering "these two worked
                // and that one did not" would be describing a state that never exists.
                return Refused(resolution);
            }

            var change = resolution.Change!;

            // Every change in a set is resolved against the state the set started from, so two changes
            // to one reference would each be planned as if the other had not happened. Refusing is the
            // only answer that cannot silently apply something nobody asked for.
            if (!change.CreatesReference && !touched.Add((change.Target.Kind, change.Target.ReferenceId)))
            {
                return Invalid("conflicting_changes");
            }

            // Two changes can also collide without sharing a reference, by both claiming one term.
            // Each was resolved against a state in which that term was free, so left to execution they
            // would violate the filtered uniqueness index halfway through a transaction.
            foreach (var absence in resolution.ExpectedAbsences ?? [])
            {
                if (!claimedTerms.Add(absence))
                {
                    return Invalid("conflicting_changes");
                }

                absences.Add(absence);
            }

            changes.Add(change);

            foreach (var version in resolution.ExpectedVersions ?? [])
            {
                versions[(version.Kind, version.ReferenceId)] = version;
            }
        }

        var requiresConfirmation = changes.Count > 1
            || changes.Any(change => ReferenceAdministrationFacts.RequiresConfirmation(change.Kind));

        return requiresConfirmation
            ? await ProposeAsync(
                participantId, inventoryId, turnId, channelConversationId, changes, versions.Values.ToList(), absences, now, cancellationToken)
            : await ApplyImmediatelyAsync(
                participantId, inventoryId, turnId, operationId, changes, versions.Values.ToList(), absences, now, cancellationToken);
    }

    private async Task<ReferenceAdministrationResult> ProposeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        string channelConversationId,
        IReadOnlyList<ProposedReferenceChange> changes,
        IReadOnlyList<ExpectedReferenceVersion> versions,
        IReadOnlyList<ExpectedTermAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Issued here, hashed into the proposal, and returned in the answer. The proposal row itself
        // never holds anything that could approve it.
        var token = ConfirmationToken.Issue();

        var proposal = ConfirmationProposal.CreateForReferences(
            ConfirmationToken.HashOf(token),
            participantId,
            channelConversationId,
            inventoryId,
            turnId,
            changes,
            versions,
            absences,
            now);

        // Storing supersedes whatever was pending in this conversation - a stock proposal just as much
        // as an administration one - atomically, so a confirmation arriving now can only ever mean
        // this proposal.
        await proposalStore.StoreAsync(proposal, now, cancellationToken);

        return new ReferenceAdministrationResult(
            ReferenceAdministrationResultKind.ConfirmationRequired,
            "confirmation_required",
            Proposal: new ReferenceProposalView(
                token,
                proposal.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                proposal.ReferenceChanges.Select(ToChangeView).ToList()));
    }

    private async Task<ReferenceAdministrationResult> ApplyImmediatelyAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        TurnId turnId,
        ReferenceOperationId operationId,
        IReadOnlyList<ProposedReferenceChange> changes,
        IReadOnlyList<ExpectedReferenceVersion> versions,
        IReadOnlyList<ExpectedTermAbsence> absences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var stored = await administrationStore.ApplyAsync(
            new ReferenceChangeSetCommand
            {
                OperationId = operationId,
                InventoryId = inventoryId,
                ActorId = participantId,
                ConfirmedByTurnId = turnId,
                ConsumesProposalId = null,
                Changes = changes,
                ExpectedVersions = versions,
                ExpectedTermAbsences = absences,
                Now = now,
            },
            cancellationToken);

        return stored.Outcome == ReferenceAdministrationStoreOutcome.Conflict
            ? new ReferenceAdministrationResult(ReferenceAdministrationResultKind.Conflict, "state_changed")
            : Applied(stored.Recorded!);
    }

    /// <summary>
    /// The one place an applied administration change set becomes an answer, so a replay served from
    /// the ledger, a store that converged on an already-applied operation, and a first attempt that
    /// has just written are literally indistinguishable to a Participant.
    /// </summary>
    internal static ReferenceAdministrationResult Applied(RecordedReferenceChangeSet recorded) => new(
        ReferenceAdministrationResultKind.Completed,
        "completed",
        new ReferenceChangeSetView(recorded.Changes.Select(ToChangeView).ToList()));

    internal static ReferenceChangeView ToChangeView(RecordedReferenceChange change) => new(
        change.Order,
        ReferenceAdministrationFacts.ToMachineText(change.Kind),
        change.ReferenceKind.ToString().ToLowerInvariant(),
        change.ReferenceId.ToString(),
        change.Name,
        change.NewName,
        change.Alias,
        change.Aliases);

    internal static ReferenceChangeView ToChangeView(ProposedReferenceChange change) => new(
        change.Order,
        ReferenceAdministrationFacts.ToMachineText(change.Kind),
        change.Target.Kind.ToString().ToLowerInvariant(),
        change.Target.ReferenceId.ToString(),
        change.Target.Name,
        change.NewName,
        change.Term?.Term,
        [.. change.Terms.Where(term => !term.IsCanonical).Select(term => term.Term)]);

    private static ReferenceAdministrationResult Refused(ReferenceChangeResolution resolution) => resolution.Kind switch
    {
        ReferenceChangeResolutionKind.NotFound => new(ReferenceAdministrationResultKind.NotFound, resolution.Code),
        ReferenceChangeResolutionKind.ReferenceNotFound => new(
            ReferenceAdministrationResultKind.ReferenceNotFound,
            resolution.Code,
            UnresolvedReference: resolution.UnresolvedReference,
            Suggestions: resolution.Suggestions),
        ReferenceChangeResolutionKind.Conflict => new(ReferenceAdministrationResultKind.Conflict, resolution.Code),
        _ => new(ReferenceAdministrationResultKind.Invalid, resolution.Code),
    };

    private static ReferenceAdministrationResult Invalid(string code) =>
        new(ReferenceAdministrationResultKind.Invalid, code);
}
