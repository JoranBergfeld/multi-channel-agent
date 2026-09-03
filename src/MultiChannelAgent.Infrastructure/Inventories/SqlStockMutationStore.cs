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
    public async Task<StockMutationStoreResult> ApplyAsync(StockMutationCommand command, CancellationToken cancellationToken)
    {
        var alreadyApplied = await db.StockOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == command.OperationId.Value, cancellationToken);

        if (alreadyApplied is not null)
        {
            return new StockMutationStoreResult(StockMutationStoreOutcome.AlreadyApplied, ToRecorded(alreadyApplied));
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
        catch (DbUpdateConcurrencyException)
        {
            // A competing writer committed against this same row between the read above and this
            // save. Nothing here was persisted, so the ledger row and the audit fact staged alongside
            // it are discarded together with the change.
            db.ChangeTracker.Clear();
            return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
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

            if (await EquivalentExistsAsync(command.InventoryId, entry, cancellationToken))
            {
                return new StockMutationStoreResult(StockMutationStoreOutcome.StateChanged, null);
            }

            throw;
        }

        return new StockMutationStoreResult(StockMutationStoreOutcome.Applied, recorded);
    }

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
