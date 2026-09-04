using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How a reference change set was settled by the store.</summary>
public enum ReferenceAdministrationStoreOutcome
{
    /// <summary>Every change was applied, and the state changes, audit facts, ledger, proposal consumption, and any retirement-driven invalidation committed together.</summary>
    Applied,

    /// <summary>This operation identity had already been applied; the recorded changes are returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>Current state no longer matches what was proposed, or the proposal was already consumed, or a Retire is now blocked. Nothing at all was applied.</summary>
    Conflict,
}

/// <summary>
/// What one change actually did. Deliberately semantic: no row versions, concurrency stamps, audit
/// identities, or SQL detail ever appear here.
/// </summary>
public sealed record RecordedReferenceChange(
    int Order,
    ReferenceChangeKind Kind,
    ReferenceKind ReferenceKind,
    Guid ReferenceId,
    string Name)
{
    /// <summary>The exact new display name a rename applied, or null for every other kind.</summary>
    public string? NewName { get; init; }

    /// <summary>The single alias an alias add established or an alias removal ended, or null for every other kind.</summary>
    public string? Alias { get; init; }

    /// <summary>The initial aliases a Unit creation established, in order; empty for every other kind.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>Everything a retry of one applied change set must be able to re-report without touching reference data again.</summary>
public sealed record RecordedReferenceChangeSet(
    ReferenceOperationId OperationId, ProposalId? ProposalId, IReadOnlyList<RecordedReferenceChange> Changes);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="ReferenceAdministrationStoreOutcome.Conflict"/>.</summary>
public sealed record ReferenceAdministrationStoreResult(
    ReferenceAdministrationStoreOutcome Outcome, RecordedReferenceChangeSet? Recorded);

/// <summary>
/// One fully decided set of administration changes, ready to apply. Everything is already resolved:
/// each <see cref="ProposedReferenceChange"/> names its exact identity, names, and terms, and the
/// expected versions and term absences say exactly what current state must still look like.
/// </summary>
public sealed record ReferenceChangeSetCommand
{
    /// <summary>The retry-stable identity this execution is recorded under; the reference ledger is keyed by it.</summary>
    public required ReferenceOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose role authorized this; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    /// <summary>
    /// The Turn that caused this execution. Recorded and uniquely indexed per Inventory, so a Turn
    /// re-driven after a crash finds what its own first attempt did without needing the proposal -
    /// which, by then, has been consumed.
    /// </summary>
    public required TurnId ConfirmedByTurnId { get; init; }

    /// <summary>The proposal to consume in the very same transaction, or null for an immediate change that needed none.</summary>
    public ProposalId? ConsumesProposalId { get; init; }

    public required IReadOnlyList<ProposedReferenceChange> Changes { get; init; }

    public required IReadOnlyList<ExpectedReferenceVersion> ExpectedVersions { get; init; }

    public required IReadOnlyList<ExpectedTermAbsence> ExpectedTermAbsences { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The single atomic writer for one or many reference administration changes. One call must, in one
/// transaction:
///
/// <list type="number">
/// <item>refuse if this operation identity was already applied, returning what it did;</item>
/// <item>consume the proposal it names, refusing if something already did;</item>
/// <item>refuse if any touched Unit or Location no longer carries its expected version;</item>
/// <item>refuse if any expected term absence has since been filled;</item>
/// <item><b>re-check every Retire against current Stock Entries</b> - this, not the plan-time check, is what "confirmed Retire fails for currently referenced data" means;</item>
/// <item>apply every change, preserving the identity of everything it retires;</item>
/// <item>append one minimal semantic audit fact per change;</item>
/// <item>settle every <em>other</em> pending proposal - stock proposals included - that references an identity this set retired;</item>
/// <item>and record the ledger.</item>
/// </list>
///
/// Partial application is never acceptable. A caller that sees
/// <see cref="ReferenceAdministrationStoreOutcome.Conflict"/> must be able to rely on nothing at all
/// having happened, which is exactly what "a failed atomic batch changes nothing" means.
/// </summary>
public interface IReferenceAdministrationStore
{
    /// <summary>What this operation identity already did in this Inventory, or null when it has never been applied there.</summary>
    Task<RecordedReferenceChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, ReferenceOperationId operationId, CancellationToken cancellationToken);

    /// <summary>
    /// What this Turn already did in this Inventory, or null when it did nothing. This is the replay
    /// lookup, keyed by the Turn rather than by the operation identity, because a confirmation
    /// consumes its proposal and a re-driven Turn can no longer derive that identity.
    /// </summary>
    Task<RecordedReferenceChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken);

    Task<ReferenceAdministrationStoreResult> ApplyAsync(
        ReferenceChangeSetCommand command, CancellationToken cancellationToken);
}
