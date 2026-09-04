using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IStockMutationStore"/> for Application-layer unit tests. It applies the
/// same rules the SQL store must: an operation identity already in the ledger re-reports its recorded
/// effect and applies nothing; a target that no longer carries the Quantity the caller planned against
/// is refused; and every applied mutation appends exactly one audit fact. It writes through the same
/// <see cref="InMemoryStockStore"/> the reads come from, so a test sees one consistent Inventory.
/// </summary>
public sealed class InMemoryStockMutationStore(InMemoryStockStore stockStore) : IStockMutationStore
{
    private readonly Dictionary<StockOperationId, (InventoryId InventoryId, RecordedStockMutation Recorded)> _ledger = [];
    private readonly Dictionary<UnitId, string> _unitNames = [];
    private readonly Dictionary<LocationId, string> _locationNames = [];

    /// <summary>Every audit fact appended so far, in order, so a test can assert exactly one per applied mutation.</summary>
    public List<AuditFact> AuditFacts { get; } = [];

    /// <summary>Simulates a competing writer having changed the target since the caller planned.</summary>
    public bool ForceStateChanged { get; set; }

    public void NameUnit(UnitId unitId, string canonicalName) => _unitNames[unitId] = canonicalName;

    public void NameLocation(LocationId locationId, string name) => _locationNames[locationId] = name;

    /// <summary>
    /// Scoped to the Inventory exactly as the SQL store's ledger lookup is, so a recorded operation is
    /// invisible from any other Inventory even under the same operation identity.
    /// </summary>
    public Task<RecordedStockMutation?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken) =>
        Task.FromResult(
            _ledger.TryGetValue(operationId, out var entry) && entry.InventoryId == inventoryId ? entry.Recorded : null);

    public async Task<StockMutationStoreResult> ApplyAsync(StockMutationCommand command, CancellationToken cancellationToken)
    {
        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } alreadyRecorded)
        {
            return new StockMutationStoreResult(StockMutationStoreOutcome.AlreadyApplied, alreadyRecorded);
        }

        // The ledger's key is the operation identity alone, exactly as the SQL table's primary key is,
        // so reusing one identity across Inventories fails loudly here too rather than quietly
        // overwriting another Inventory's record.
        if (_ledger.ContainsKey(command.OperationId))
        {
            throw new InvalidOperationException(
                $"Operation {command.OperationId} is already recorded against a different Inventory.");
        }

        if (ForceStateChanged)
        {
            return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
        }

        StockEntrySummary row;
        Quantity previousQuantity;

        if (command.StockEntryId is { } targetId)
        {
            var current = stockStore.Find(command.InventoryId, targetId);
            if (current is null || current.Quantity != command.ExpectedQuantity)
            {
                return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
            }

            previousQuantity = current.Quantity;
            row = stockStore.SetQuantity(command.InventoryId, targetId, command.ResultingQuantity)!;
        }
        else
        {
            previousQuantity = Quantity.Zero;
            var unitId = command.NewEntryUnitId!.Value;
            row = stockStore.CreateRow(
                command.InventoryId,
                command.NewEntryName!,
                unitId,
                _unitNames.GetValueOrDefault(unitId, "each"),
                command.NewEntryLocationId,
                command.NewEntryLocationId is { } locationId ? _locationNames.GetValueOrDefault(locationId) : null,
                command.Note,
                command.ResultingQuantity);
        }

        var recorded = new RecordedStockMutation(
            row.Id,
            row.Name,
            row.UnitCanonicalName,
            row.LocationName,
            row.Note,
            previousQuantity,
            row.Quantity,
            CreatedEntry: command.StockEntryId is null,
            command.NotePreserved);

        _ledger[command.OperationId] = (command.InventoryId, recorded);
        AuditFacts.Add(AuditFact.Create(
            StockAuditFacts.EventTypeFor(command.Kind),
            AuditActorKind.Participant,
            command.ActorId.ToString(),
            command.InventoryId,
            subjectParticipantId: null,
            StockAuditFacts.OutcomeCodeFor(command.Kind, command.StockEntryId is null),
            command.Now));

        return new StockMutationStoreResult(StockMutationStoreOutcome.Applied, recorded);
    }
}
