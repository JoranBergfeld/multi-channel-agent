using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>How an import execution was settled.</summary>
public enum ImportExecutionOutcome
{
    /// <summary>Every Stock Entry was created, with its audit fact, its ledger row, and the raw file discarded - all together.</summary>
    Applied,

    /// <summary>This operation identity had already been applied; the recorded facts are returned unchanged.</summary>
    AlreadyApplied,

    /// <summary>The Inventory was no longer empty, or the proposal was no longer pending. Nothing at all was written.</summary>
    Conflict,
}

/// <summary>
/// The durable semantic facts of one applied import - exactly what a replay must be able to
/// re-report without touching Inventory state again. Deliberately semantic: no row versions, no audit
/// identities, no SQL detail, and no file contents.
/// </summary>
public sealed record RecordedImport(
    ImportOperationId OperationId, ImportProposalId ProposalId, FileDigest FileDigest, int CreatedEntryCount);

/// <summary>The store's answer; <see cref="Recorded"/> is present exactly when the outcome is not <see cref="ImportExecutionOutcome.Conflict"/>.</summary>
public sealed record ImportExecutionResult(ImportExecutionOutcome Outcome, RecordedImport? Recorded);

/// <summary>One fully decided import, ready to apply. Everything in it was reviewed; nothing is recomputed.</summary>
public sealed record ImportExecutionCommand
{
    /// <summary>The retry-stable identity this execution is recorded under; the ledger is keyed by it.</summary>
    public required ImportOperationId OperationId { get; init; }

    public required InventoryId InventoryId { get; init; }

    /// <summary>The Participant whose Editor-or-better Membership authorized this; recorded as the audit actor.</summary>
    public required ParticipantId ActorId { get; init; }

    /// <summary>The proposal to consume in the very same transaction, and whose raw upload to discard.</summary>
    public required ImportProposalId ConsumesProposalId { get; init; }

    public required FileDigest FileDigest { get; init; }

    public required IReadOnlyList<ImportEntry> Entries { get; init; }

    /// <summary>The empty state this import was decided against, re-asserted inside the execution transaction.</summary>
    public required EmptyStateVersion EmptyStateVersion { get; init; }

    public required DateTimeOffset Now { get; init; }
}

/// <summary>
/// The one atomic writer behind Initial Import.
///
/// <see cref="ApplyAsync"/> holds every Unit and Location the entries reference, consumes the
/// proposal, re-asserts that the Inventory still holds no Stock Entry at all, creates every entry,
/// appends exactly one minimal semantic audit fact, writes its ledger row, and discards the raw
/// upload - in one transaction. A caller that sees <see cref="ImportExecutionOutcome.Conflict"/> may
/// rely on nothing at all having happened, including the proposal still being pending.
/// </summary>
public interface IImportExecutionStore
{
    Task<RecordedImport?> FindRecordedAsync(
        InventoryId inventoryId, ImportOperationId operationId, CancellationToken cancellationToken);

    Task<ImportExecutionResult> ApplyAsync(ImportExecutionCommand command, CancellationToken cancellationToken);
}
