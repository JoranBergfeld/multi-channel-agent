using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockMutationStore"/>. The Stock Entry change, its minimal semantic
/// audit fact, and its operation ledger row are staged against one
/// <see cref="MultiChannelAgentDbContext"/> and committed by a single
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call, which the provider executes as
/// one transaction: current state and the audit of it can never disagree, and a mutation can never be
/// applied without its ledger row - the very row that stops a retry applying it again.
///
/// The ledger row commits with the state change rather than after it deliberately. The terminal
/// Outcome, the Delivery, and inbox completion are written later, by
/// <see cref="Application.Turns.ITurnResultStore"/>, in their own single atomic write; if the process
/// dies in between, the Turn is reprocessed, derives the same
/// <see cref="StockOperationId"/>, finds this ledger row, and re-reports the effect instead of
/// applying a second one. That is what makes the two writes safe to be two.
/// </summary>
public sealed class SqlStockMutationStore(MultiChannelAgentDbContext db) : IStockMutationStore
{
    public async Task<RecordedStockMutation?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken)
    {
        // Scoped to the Inventory from trusted context as well as the operation identity, so a ledger
        // row can only ever be found from the Inventory it was actually applied to.
        var recorded = await db.StockOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.OperationId == operationId.Value && o.InventoryId == inventoryId.Value, cancellationToken);

        return recorded is null ? null : ToRecorded(recorded);
    }

    public async Task<StockMutationStoreResult> ApplyAsync(StockMutationCommand command, CancellationToken cancellationToken)
    {
        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } alreadyApplied)
        {
            return new StockMutationStoreResult(StockMutationStoreOutcome.AlreadyApplied, alreadyApplied);
        }

        return command.StockEntryId is { } targetId
            ? await ChangeAsync(command, targetId, cancellationToken)
            : await CreateAsync(command, cancellationToken);
    }

    private async Task<StockMutationStoreResult> ChangeAsync(
        StockMutationCommand command, StockEntryId targetId, CancellationToken cancellationToken)
    {
        var entry = await db.StockEntries.FirstOrDefaultAsync(
            e => e.Id == targetId.Value && e.InventoryId == command.InventoryId.Value, cancellationToken);

        // The caller decided this change against a Quantity it read a moment ago. If the row is gone,
        // or no longer carries that Quantity, a competing writer got there first: the decision is
        // stale, so it is refused rather than applied on top of a state nobody chose.
        if (entry is null || Quantity.Create(entry.Quantity) != command.ExpectedQuantity)
        {
            db.ChangeTracker.Clear();
            return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
        }

        var unitCanonicalName = await UnitCanonicalNameAsync(entry.UnitId, cancellationToken);
        var locationName = await LocationNameAsync(entry.LocationId, cancellationToken);

        entry.Quantity = command.ResultingQuantity.Value;
        entry.ConcurrencyStamp = Guid.NewGuid();

        var recorded = new RecordedStockMutation(
            new StockEntryId(entry.Id),
            entry.Name,
            unitCanonicalName,
            locationName,
            entry.Note,
            command.ExpectedQuantity!.Value,
            command.ResultingQuantity,
            CreatedEntry: false,
            command.NotePreserved);

        StageLedgerAndAudit(command, recorded, createdEntry: false);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Nothing here was persisted, so the ledger row and the audit fact staged alongside the
            // change are discarded together with it.
            db.ChangeTracker.Clear();

            // A competing writer may have been this very operation, applied by another replica between
            // the lookup above and this save. Its ledger row is the authoritative record of what
            // happened, so converge on re-reporting it rather than claiming a conflict.
            if (await ConvergedOnAlreadyAppliedAsync(command, cancellationToken) is { } converged)
            {
                return converged;
            }

            // Otherwise a different writer committed against this same row first, so this decision is
            // stale. Anything that is neither of those is a real fault and must keep propagating.
            if (exception is DbUpdateConcurrencyException)
            {
                return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
            }

            throw;
        }

        return new StockMutationStoreResult(StockMutationStoreOutcome.Applied, recorded);
    }

    private async Task<StockMutationStoreResult> CreateAsync(StockMutationCommand command, CancellationToken cancellationToken)
    {
        // The domain factory validates and normalizes the name and Note, so persistence never sees a
        // value the domain would have refused.
        var entry = StockEntry.Create(
            command.InventoryId,
            command.NewEntryUnitId!.Value,
            command.NewEntryLocationId,
            command.NewEntryName,
            command.Note,
            command.ResultingQuantity,
            command.Now);

        var unitCanonicalName = await UnitCanonicalNameAsync(entry.UnitId.Value, cancellationToken);
        var locationName = await LocationNameAsync(entry.LocationId?.Value, cancellationToken);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = entry.Id.Value,
            InventoryId = entry.InventoryId.Value,
            UnitId = entry.UnitId.Value,
            LocationId = entry.LocationId?.Value,
            Name = entry.Name,
            NormalizedName = entry.NormalizedName,
            Note = entry.Note,
            Quantity = entry.Quantity.Value,
            CreatedAt = entry.CreatedAt,
        });

        var recorded = new RecordedStockMutation(
            entry.Id,
            entry.Name,
            unitCanonicalName,
            locationName,
            entry.Note,
            Quantity.Zero,
            entry.Quantity,
            CreatedEntry: true,
            command.NotePreserved);

        StageLedgerAndAudit(command, recorded, createdEntry: true);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Equivalent Stock is unique in the database, so a competing writer that created this very
            // Stock Entry first makes this insert fail. Classify that as the state having changed only
            // when the equivalent row genuinely now exists; any other failure is a real fault and must
            // keep propagating rather than being reported as a routine conflict.
            db.ChangeTracker.Clear();

            // The competing writer may have been this very operation, applied by another replica
            // between the lookup above and this save. Re-report what it recorded rather than treating
            // this operation's own effect as somebody else's conflict.
            if (await ConvergedOnAlreadyAppliedAsync(command, cancellationToken) is { } converged)
            {
                return converged;
            }

            if (await EquivalentExistsAsync(command.InventoryId, entry, cancellationToken))
            {
                return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
            }

            throw;
        }

        return new StockMutationStoreResult(StockMutationStoreOutcome.Applied, recorded);
    }

    /// <summary>
    /// Whether this operation identity turned out to be already recorded after all - the convergence a
    /// failed save must check first, so a replica that lost the race to its own twin re-reports that
    /// twin's effect instead of reporting a conflict against itself. Returns null when this operation
    /// is genuinely not in the ledger, leaving the caller to classify the failure on its own terms.
    /// </summary>
    private async Task<StockMutationStoreResult?> ConvergedOnAlreadyAppliedAsync(
        StockMutationCommand command, CancellationToken cancellationToken) =>
        await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } recorded
            ? new StockMutationStoreResult(StockMutationStoreOutcome.AlreadyApplied, recorded)
            : null;

    private void StageLedgerAndAudit(StockMutationCommand command, RecordedStockMutation recorded, bool createdEntry)
    {
        db.StockOperations.Add(new StockOperationEntity
        {
            OperationId = command.OperationId.Value,
            InventoryId = command.InventoryId.Value,
            Kind = command.Kind.ToString(),
            StockEntryId = recorded.StockEntryId.Value,
            Name = recorded.Name,
            UnitCanonicalName = recorded.UnitCanonicalName,
            LocationName = recorded.LocationName,
            Note = recorded.Note,
            PreviousQuantity = recorded.PreviousQuantity.Value,
            ResultingQuantity = recorded.ResultingQuantity.Value,
            CreatedEntry = recorded.CreatedEntry,
            NotePreserved = recorded.NotePreserved,
            AppliedAt = command.Now,
        });

        db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
            StockAuditFacts.EventTypeFor(command.Kind),
            AuditActorKind.Participant,
            command.ActorId.ToString(),
            command.InventoryId,
            subjectParticipantId: null,
            StockAuditFacts.OutcomeCodeFor(command.Kind, createdEntry),
            command.Now)));
    }

    /// <summary>
    /// Whether the exact Equivalent Stock this create aimed at now exists. Unlocated Stock is the
    /// absence of a Location, so it is asked for as such rather than compared to a null parameter,
    /// which relational NULL semantics would never match.
    /// </summary>
    private async Task<bool> EquivalentExistsAsync(InventoryId inventoryId, StockEntry entry, CancellationToken cancellationToken)
    {
        var rows = db.StockEntries.AsNoTracking().Where(e =>
            e.InventoryId == inventoryId.Value
            && e.NormalizedName == entry.NormalizedName
            && e.UnitId == entry.UnitId.Value);

        rows = entry.LocationId is { } locationId
            ? rows.Where(e => e.LocationId == locationId.Value)
            : rows.Where(e => e.LocationId == null);

        return await rows.AnyAsync(cancellationToken);
    }

    private async Task<string> UnitCanonicalNameAsync(Guid unitId, CancellationToken cancellationToken) =>
        await db.Units.AsNoTracking().Where(u => u.Id == unitId).Select(u => u.CanonicalName).FirstAsync(cancellationToken);

    private async Task<string?> LocationNameAsync(Guid? locationId, CancellationToken cancellationToken) =>
        locationId is { } id
            ? await db.Locations.AsNoTracking().Where(l => l.Id == id).Select(l => l.Name).FirstAsync(cancellationToken)
            : null;

    private static RecordedStockMutation ToRecorded(StockOperationEntity entity) => new(
        new StockEntryId(entity.StockEntryId),
        entity.Name,
        entity.UnitCanonicalName,
        entity.LocationName,
        entity.Note,
        Quantity.Create(entity.PreviousQuantity),
        Quantity.Create(entity.ResultingQuantity),
        entity.CreatedEntry,
        entity.NotePreserved);
}
