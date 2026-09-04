using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How a change set was settled by the store.</summary>
public enum StockChangeSetStoreOutcome
{
    /// <summary>Every change was applied, and the state changes, audit facts, ledger, and proposal consumption committed together.</summary>
    Applied,

    /// <summary>This operation identity had already been applied; the recorded effects are returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>Current state no longer matches what was proposed, or the proposal was already consumed. Nothing at all was applied.</summary>
    Conflict,
}

/// <summary>
/// One Stock Entry as it stood before and after one applied change. Deliberately semantic: no row
/// versions, concurrency stamps, audit identities, or SQL detail ever appear here.
/// </summary>
public sealed record RecordedEntryState(
    StockEntryId StockEntryId,
    string Name,
    string UnitCanonicalName,
    string? LocationName,
    Quantity PreviousQuantity,
    Quantity ResultingQuantity,
    bool Retired);

/// <summary>
/// What one change actually did. <see cref="SurvivingStockEntryId"/> and
/// <see cref="RetiredStockEntryId"/> are the answer to "which identity survived and which one was
/// retired", which every merge-retiring Move and Rename owes the Participant.
/// </summary>
public sealed record RecordedStockChangeEffect(
    int Order,
    StockMutationKind Kind,
    StockChangeEffectKind Effect,
    RecordedEntryState Source,
    RecordedEntryState? Destination,
    Quantity TransferredQuantity)
{
    /// <summary>The exact new display name a Rename applied, or null for every other effect.</summary>
    public string? NewName { get; init; }

    /// <summary>
    /// The Stock Entry that still exists once this change was applied: the destination when a merge
    /// retired the source, the entry itself otherwise. Null for a Forget, which leaves nothing
    /// behind - reporting the forgotten entry as its own survivor would be the one lie this record
    /// could tell.
    /// </summary>
    public StockEntryId? SurvivingStockEntryId =>
        StockAuditFacts.RetiresSource(Effect) ? Destination?.StockEntryId : Source.StockEntryId;

    public StockEntryId? RetiredStockEntryId => Source.Retired ? Source.StockEntryId : null;
}

/// <summary>Everything a retry of one applied change set must be able to re-report without touching Inventory state again.</summary>
public sealed record RecordedStockChangeSet(
    StockOperationId OperationId, ProposalId? ProposalId, IReadOnlyList<RecordedStockChangeEffect> Effects);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="StockChangeSetStoreOutcome.Conflict"/>.</summary>
public sealed record StockChangeSetStoreResult(StockChangeSetStoreOutcome Outcome, RecordedStockChangeSet? Recorded);

/// <summary>
/// One fully decided set of changes, ready to apply. Everything ambiguous has already been resolved:
/// each <see cref="ProposedChange"/> names its exact targets, amounts, and effect, and the expected
/// versions and absences say exactly what current state must still look like.
/// </summary>
public sealed record StockChangeSetCommand
{
    /// <summary>The retry-stable identity this execution is recorded under; the ledger is keyed by it.</summary>
    public required StockOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose Editor-or-better Membership authorized this; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    /// <summary>
    /// The Turn that caused this execution. Recorded and uniquely indexed per Inventory, so a Turn
    /// re-driven after a crash finds what its own first attempt did without needing the proposal -
    /// which, by then, has been consumed.
    /// </summary>
    public required TurnId ConfirmedByTurnId { get; init; }

    /// <summary>The proposal to consume in the very same transaction, or null for an immediate change that needed none.</summary>
    public ProposalId? ConsumesProposalId { get; init; }

    public required IReadOnlyList<ProposedChange> Changes { get; init; }

    public required IReadOnlyList<ExpectedEntryVersion> ExpectedVersions { get; init; }

    public required IReadOnlyList<ExpectedEquivalentStockAbsence> ExpectedAbsences { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The single atomic writer for one or many stock changes. One call must, in one transaction: refuse
/// if this operation identity was already applied (returning what it did), consume the proposal it
/// names (refusing if something already did), refuse if any touched row no longer carries its
/// expected version, and otherwise apply every change, append one minimal semantic audit fact per
/// change, and record the ledger - together.
///
/// Partial application is never acceptable. A caller that sees
/// <see cref="StockChangeSetStoreOutcome.Conflict"/> must be able to rely on nothing at all having
/// happened, which is exactly what "a failed atomic batch changes nothing" means.
/// </summary>
public interface IStockChangeSetStore
{
    /// <summary>
    /// What this operation identity already did in this Inventory, or null when it has never been
    /// applied there. Scoped to the Inventory from trusted context, so a recorded operation can never
    /// be re-reported into - or disclosed through - a different Inventory.
    /// </summary>
    Task<RecordedStockChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken);

    /// <summary>
    /// What this Turn already did in this Inventory, or null when it did nothing.
    ///
    /// This is the replay lookup, and it is deliberately keyed by the Turn rather than by the
    /// operation identity: a confirmation consumes its proposal, so a Turn re-driven after a crash
    /// between the mutation transaction and the Outcome transaction can no longer find the proposal
    /// its identity would have been derived from. Asking "what did this Turn already do here" needs
    /// nothing but trusted context, and answering from it is what stops a completed mutation ever
    /// being re-planned.
    /// </summary>
    Task<RecordedStockChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken);

    Task<StockChangeSetStoreResult> ApplyAsync(StockChangeSetCommand command, CancellationToken cancellationToken);
}
