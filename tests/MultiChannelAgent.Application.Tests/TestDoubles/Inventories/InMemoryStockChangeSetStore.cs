using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// In-memory <see cref="IStockChangeSetStore"/> applying the same rules the SQL store must: an
/// operation identity already in the ledger re-reports and applies nothing; a proposal that is not
/// consumable is a conflict; a row whose version moved is a conflict; and a conflict applies nothing
/// at all. It writes through the same <see cref="InMemoryStockStore"/> the reads come from, so a test
/// sees one consistent Inventory.
/// </summary>
public sealed class InMemoryStockChangeSetStore(InMemoryStockStore stockStore, InMemoryConfirmationProposalStore proposalStore)
    : IStockChangeSetStore
{
    private readonly Dictionary<StockOperationId, (InventoryId InventoryId, TurnId TurnId, RecordedStockChangeSet Recorded)> _ledger = [];

    /// <summary>Every audit fact appended so far, in order, so a test can assert exactly one per applied change.</summary>
    public List<AuditFact> AuditFacts { get; } = [];

    /// <summary>Simulates a competing writer having moved a touched row since the caller planned.</summary>
    public bool ForceConflict { get; set; }

    public Task<RecordedStockChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken) =>
        Task.FromResult(
            _ledger.TryGetValue(operationId, out var entry) && entry.InventoryId == inventoryId ? entry.Recorded : null);

    public Task<RecordedStockChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken) =>
        Task.FromResult(_ledger.Values
            .Where(entry => entry.InventoryId == inventoryId && entry.TurnId == turnId)
            .Select(entry => entry.Recorded)
            .SingleOrDefault());

    public async Task<StockChangeSetStoreResult> ApplyAsync(StockChangeSetCommand command, CancellationToken cancellationToken)
    {
        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.AlreadyApplied, already);
        }

        if (ForceConflict)
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
        }

        // Consumed first, and guarded, exactly as the SQL store consumes it inside its transaction:
        // two confirmations of one proposal can never both execute.
        if (command.ConsumesProposalId is { } proposalId
            && !await proposalStore.SettleAsync(proposalId, ProposalStatus.Confirmed, command.Now, cancellationToken))
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
        }

        foreach (var version in command.ExpectedVersions)
        {
            var current = stockStore.Find(command.InventoryId, version.StockEntryId);
            if (current is null || stockStore.VersionOf(command.InventoryId, version.StockEntryId) != version.ConcurrencyStamp)
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
            }
        }

        foreach (var absence in command.ExpectedAbsences)
        {
            if (stockStore.FindEquivalent(command.InventoryId, absence.NormalizedName, absence.UnitId, absence.LocationId) is not null)
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
            }
        }

        var effects = command.Changes
            .OrderBy(change => change.Order)
            .Select(change => Apply(command.InventoryId, change))
            .ToList();

        foreach (var change in command.Changes)
        {
            AuditFacts.Add(AuditFact.Create(
                StockAuditFacts.EventTypeFor(change.Kind),
                AuditActorKind.Participant,
                command.ActorId.ToString(),
                command.InventoryId,
                subjectParticipantId: null,
                StockAuditFacts.OutcomeCodeFor(change.Effect),
                command.Now));
        }

        var recorded = new RecordedStockChangeSet(command.OperationId, command.ConsumesProposalId, effects);
        _ledger[command.OperationId] = (command.InventoryId, command.ConfirmedByTurnId, recorded);

        return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Applied, recorded);
    }

    private RecordedStockChangeEffect Apply(InventoryId inventoryId, ProposedChange change)
    {
        switch (change.Effect)
        {
            case StockChangeEffectKind.Created:
            {
                var created = stockStore.CreateRow(
                    inventoryId,
                    change.Source.Name,
                    change.Source.UnitId,
                    change.Source.UnitCanonicalName,
                    change.Source.LocationId,
                    change.Source.LocationName,
                    change.Source.Note,
                    change.Source.ResultingQuantity);

                return Effect(change, Recorded(created, Quantity.Zero, retired: false), null);
            }

            case StockChangeEffectKind.QuantityIncreased:
            case StockChangeEffectKind.QuantityDecreased:
            case StockChangeEffectKind.QuantitySet:
            case StockChangeEffectKind.QuantityCleared:
            {
                var updated = stockStore.SetQuantity(inventoryId, change.Source.StockEntryId!.Value, change.Source.ResultingQuantity)!;
                return Effect(change, Recorded(updated, change.Source.PreviousQuantity, retired: false), null);
            }

            case StockChangeEffectKind.Placed:
            {
                var moved = stockStore.Relocate(
                    inventoryId, change.Source.StockEntryId!.Value, change.Destination!.LocationId, change.Destination.LocationName);

                // Origin, then destination - the same shape SqlStockChangeSetStore records.
                return Effect(
                    change,
                    Recorded(change.Source),
                    Recorded(moved, change.Source.PreviousQuantity, retired: false));
            }

            case StockChangeEffectKind.Split:
            {
                var remainder = stockStore.SetQuantity(inventoryId, change.Source.StockEntryId!.Value, change.Source.ResultingQuantity)!;
                var destination = stockStore.CreateRow(
                    inventoryId,
                    change.Destination!.Name,
                    change.Destination.UnitId,
                    change.Destination.UnitCanonicalName,
                    change.Destination.LocationId,
                    change.Destination.LocationName,
                    change.Destination.Note,
                    change.Destination.ResultingQuantity);

                return Effect(
                    change,
                    Recorded(remainder, change.Source.PreviousQuantity, retired: false),
                    Recorded(destination, Quantity.Zero, retired: false));
            }

            case StockChangeEffectKind.SplitMerged:
            {
                var remainder = stockStore.SetQuantity(inventoryId, change.Source.StockEntryId!.Value, change.Source.ResultingQuantity)!;
                var destination = stockStore.SetQuantity(
                    inventoryId, change.Destination!.StockEntryId!.Value, change.Destination.ResultingQuantity)!;

                return Effect(
                    change,
                    Recorded(remainder, change.Source.PreviousQuantity, retired: false),
                    Recorded(destination, change.Destination.PreviousQuantity, retired: false));
            }

            case StockChangeEffectKind.Merged:
            case StockChangeEffectKind.RenameMerged:
            {
                var destination = stockStore.SetQuantity(
                    inventoryId, change.Destination!.StockEntryId!.Value, change.Destination.ResultingQuantity)!;
                var retired = RetiredSource(change);
                stockStore.Delete(inventoryId, change.Source.StockEntryId!.Value);

                return Effect(change, retired, Recorded(destination, change.Destination.PreviousQuantity, retired: false));
            }

            case StockChangeEffectKind.Renamed:
            {
                var renamed = stockStore.Rename(
                    inventoryId, change.Source.StockEntryId!.Value, change.NewName!, change.NewNormalizedName!);

                return Effect(change, Recorded(renamed, change.Source.PreviousQuantity, retired: false), null);
            }

            case StockChangeEffectKind.Forgotten:
            {
                var retired = RetiredSource(change);
                stockStore.Delete(inventoryId, change.Source.StockEntryId!.Value);

                return Effect(change, retired, null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Effect, "Unhandled stock change effect.");
        }
    }

    private static RecordedStockChangeEffect Effect(ProposedChange change, RecordedEntryState source, RecordedEntryState? destination) =>
        new(change.Order, change.Kind, change.Effect, source, destination, change.TransferredQuantity) { NewName = change.NewName };

    private static RecordedEntryState Recorded(StockEntrySummary row, Quantity previousQuantity, bool retired) =>
        new(row.Id, row.Name, row.UnitCanonicalName, row.LocationName, previousQuantity, row.Quantity, retired);

    /// <summary>The recorded form of one proposed state, for the sides an effect does not re-read.</summary>
    private static RecordedEntryState Recorded(ProposedEntryState state) => new(
        state.StockEntryId!.Value,
        state.Name,
        state.UnitCanonicalName,
        state.LocationName,
        state.PreviousQuantity,
        state.ResultingQuantity,
        state.Retired);

    private static RecordedEntryState RetiredSource(ProposedChange change) => new(
        change.Source.StockEntryId!.Value,
        change.Source.Name,
        change.Source.UnitCanonicalName,
        change.Source.LocationName,
        change.Source.PreviousQuantity,
        Quantity.Zero,
        Retired: true);
}
