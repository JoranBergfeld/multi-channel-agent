using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IImportExecutionStore"/>. It honours exactly the contract the SQL
/// store must: replay by operation identity, single-use proposal consumption, the authoritative
/// empty-state re-check, one audit fact, and nothing written when any of them refuses.
/// </summary>
public sealed class InMemoryImportExecutionStore(
    InMemoryImportProposalStore proposalStore, InMemoryStockEmptyStateReader emptyState)
    : IImportExecutionStore
{
    private readonly Dictionary<(InventoryId, ImportOperationId), RecordedImport> _recorded = [];

    /// <summary>Every audit fact this store appended, in order - the same minimal facts the SQL store writes.</summary>
    public List<AuditFact> Audits { get; } = [];

    /// <summary>Every entry this store created, so a test can assert exactly what an import produced.</summary>
    public List<ImportEntry> CreatedEntries { get; } = [];

    public Task<RecordedImport?> FindRecordedAsync(
        InventoryId inventoryId, ImportOperationId operationId, CancellationToken cancellationToken) =>
        Task.FromResult(_recorded.GetValueOrDefault((inventoryId, operationId)));

    public async Task<ImportExecutionResult> ApplyAsync(ImportExecutionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_recorded.TryGetValue((command.InventoryId, command.OperationId), out var already))
        {
            return new ImportExecutionResult(ImportExecutionOutcome.AlreadyApplied, already);
        }

        // Both dependencies are always present (required constructor params), so this double
        // matches the SQL store's single transaction: re-assert emptiness, then consume the
        // proposal, before anything is written.
        if (await emptyState.AnyStockAsync(command.InventoryId, cancellationToken))
        {
            return new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
        }

        if (!await proposalStore.SettleAsync(
                command.ConsumesProposalId, ImportProposalStatus.Confirmed, command.Now, cancellationToken))
        {
            return new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
        }

        CreatedEntries.AddRange(command.Entries);
        emptyState.SetAnyStock(command.InventoryId, true);

        Audits.Add(AuditFact.Create(
            AuditEventType.StockImported,
            AuditActorKind.Participant,
            command.ActorId.ToString(),
            command.InventoryId,
            subjectParticipantId: null,
            ImportFacts.CompletedOutcomeCode,
            command.Now));

        var recorded = new RecordedImport(
            command.OperationId, command.ConsumesProposalId, command.FileDigest, command.Entries.Count);
        _recorded[(command.InventoryId, command.OperationId)] = recorded;

        return new ImportExecutionResult(ImportExecutionOutcome.Applied, recorded);
    }
}
