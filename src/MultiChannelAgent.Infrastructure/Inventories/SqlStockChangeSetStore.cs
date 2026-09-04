using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IStockChangeSetStore"/>: the one transaction the confirmation
/// protocol rests on.
///
/// One <see cref="ApplyAsync"/> call consumes the proposal, verifies and locks every touched row,
/// applies every effect, appends one minimal semantic audit fact per change, and writes the ledger -
/// all inside one explicit transaction. Any failure rolls the whole thing back, so a caller that
/// sees <see cref="StockChangeSetStoreOutcome.Conflict"/> can rely on nothing at all having happened,
/// including the proposal still being pending.
///
/// Locking is deliberate rather than incidental. The second pass below touches every row the change
/// set will write to, in one globally agreed order (the ordinal text of the Stock Entry identity),
/// with a single guarded UPDATE per row that both takes the row's exclusive lock and checks its
/// expected version. Two concurrent batches over overlapping rows therefore contend in the same
/// order and one of them simply loses, instead of deadlocking each other halfway through.
///
/// The ledger commits with the state change rather than after it, exactly as
/// <see cref="SqlStockMutationStore"/> does: the terminal Outcome is written later, in its own atomic
/// write, and if the process dies in between, the Turn is reprocessed, finds its ledger row through
/// <see cref="FindRecordedByTurnAsync"/>, and re-reports instead of re-applying.
/// </summary>
public sealed class SqlStockChangeSetStore(MultiChannelAgentDbContext db) : IStockChangeSetStore
{
    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);

    public async Task<RecordedStockChangeSet?> FindRecordedAsync(
        InventoryId inventoryId, StockOperationId operationId, CancellationToken cancellationToken)
    {
        var header = await db.StockChangeSetOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId.Value && o.InventoryId == inventoryId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<RecordedStockChangeSet?> FindRecordedByTurnAsync(
        InventoryId inventoryId, TurnId turnId, CancellationToken cancellationToken)
    {
        var header = await db.StockChangeSetOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.InventoryId == inventoryId.Value && o.ConfirmedByTurnId == turnId.Value, cancellationToken);

        return header is null ? null : await ReadRecordedAsync(header, cancellationToken);
    }

    public async Task<StockChangeSetStoreResult> ApplyAsync(StockChangeSetCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.AlreadyApplied, already);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            // 1. Consume the proposal, guarded. Doing this first means a losing confirmation stops
            //    here, before it has touched any Stock at all.
            if (command.ConsumesProposalId is { } proposalId)
            {
                var consumed = await db.ConfirmationProposals
                    .Where(p => p.ProposalId == proposalId.Value && p.Status == PendingStatus)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(p => p.Status, nameof(ProposalStatus.Confirmed))
                            .SetProperty(p => p.SettledAt, command.Now),
                        cancellationToken);

                if (consumed != 1)
                {
                    return await RolledBackConflictAsync(transaction, cancellationToken);
                }
            }

            // 2. Lock and verify every touched row, in one globally agreed order. Each statement both
            //    takes the row's exclusive lock and asserts the version the proposal was decided
            //    against, so a row that moved since stops the whole set here.
            foreach (var expected in command.ExpectedVersions.OrderBy(v => v.StockEntryId.Value.ToString("D"), StringComparer.Ordinal))
            {
                var freshStamp = Guid.NewGuid();

                var locked = await db.StockEntries
                    .Where(e => e.Id == expected.StockEntryId.Value
                        && e.InventoryId == command.InventoryId.Value
                        && e.ConcurrencyStamp == expected.ConcurrencyStamp)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(e => e.ConcurrencyStamp, freshStamp), cancellationToken);

                if (locked != 1)
                {
                    return await RolledBackConflictAsync(transaction, cancellationToken);
                }
            }

            // 3. Verify every expected absence. The Equivalent Stock unique indexes are the real
            //    guarantee; this check turns the common case into a clean conflict rather than an
            //    exception.
            foreach (var absence in command.ExpectedAbsences)
            {
                if (await EquivalentExistsAsync(command.InventoryId, absence, cancellationToken))
                {
                    return await RolledBackConflictAsync(transaction, cancellationToken);
                }
            }

            // 4. Apply the effects in the order the Participant reviewed.
            var effects = new List<RecordedStockChangeEffect>(command.Changes.Count);
            foreach (var change in command.Changes.OrderBy(change => change.Order))
            {
                var applied = await ApplyChangeAsync(command, change, cancellationToken);
                if (applied is null)
                {
                    return await RolledBackConflictAsync(transaction, cancellationToken);
                }

                effects.Add(applied);
            }

            // 5. Ledger, effects, and one minimal semantic audit fact per change.
            db.StockChangeSetOperations.Add(new StockChangeSetOperationEntity
            {
                OperationId = command.OperationId.Value,
                InventoryId = command.InventoryId.Value,
                ConfirmedByTurnId = command.ConfirmedByTurnId.Value,
                ProposalId = command.ConsumesProposalId?.Value,
                AppliedAt = command.Now,
            });

            foreach (var effect in effects)
            {
                db.StockChangeSetEffects.Add(ToEntity(command.OperationId, effect));
            }

            foreach (var change in command.Changes)
            {
                db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
                    StockAuditFacts.EventTypeFor(change.Kind),
                    AuditActorKind.Participant,
                    command.ActorId.ToString(),
                    command.InventoryId,
                    subjectParticipantId: null,
                    StockAuditFacts.OutcomeCodeFor(change.Effect),
                    command.Now)));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new StockChangeSetStoreResult(
                StockChangeSetStoreOutcome.Applied,
                new RecordedStockChangeSet(command.OperationId, command.ConsumesProposalId, effects));
        }
        catch (DbUpdateException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();

            // A competing writer may have been this very operation, applied by another replica. Its
            // ledger row is the authoritative record of what happened, so converge on re-reporting it
            // rather than claiming a conflict against ourselves.
            if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.AlreadyApplied, converged);
            }

            // Equivalent Stock is unique in the database, so a competing writer that created the row
            // this set meant to create makes the insert fail. That is a conflict; anything else is a
            // real fault and must keep propagating.
            if (exception is DbUpdateConcurrencyException || await AnyExpectedAbsenceFilledAsync(command, cancellationToken))
            {
                return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
            }

            throw;
        }
    }

    /// <summary>
    /// Applies exactly one decided change, or returns null when a guarded statement touched no row -
    /// which cannot happen while the lock pass holds every row, and so must fail the whole set rather
    /// than be applied partially.
    /// </summary>
    private async Task<RecordedStockChangeEffect?> ApplyChangeAsync(
        StockChangeSetCommand command, ProposedChange change, CancellationToken cancellationToken)
    {
        switch (change.Effect)
        {
            case StockChangeEffectKind.Created:
            {
                var created = StageCreate(command, change.Source);
                return Effect(change, Recorded(change.Source, created), null);
            }

            case StockChangeEffectKind.QuantityIncreased:
            case StockChangeEffectKind.QuantityDecreased:
            case StockChangeEffectKind.QuantitySet:
            case StockChangeEffectKind.QuantityCleared:
            {
                return await SetQuantityAsync(command, change.Source, cancellationToken)
                    ? Effect(change, Recorded(change.Source), null)
                    : null;
            }

            case StockChangeEffectKind.Placed:
            {
                var destination = change.Destination!;
                var updated = await db.StockEntries
                    .Where(e => e.Id == change.Source.StockEntryId!.Value.Value && e.InventoryId == command.InventoryId.Value)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(e => e.LocationId, destination.LocationId == null ? (Guid?)null : destination.LocationId.Value.Value)
                            .SetProperty(e => e.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                return updated == 1 ? Effect(change, Recorded(destination, change.Source.StockEntryId), null) : null;
            }

            case StockChangeEffectKind.Split:
            {
                if (!await SetQuantityAsync(command, change.Source, cancellationToken))
                {
                    return null;
                }

                var created = StageCreate(command, change.Destination!);
                return Effect(change, Recorded(change.Source), Recorded(change.Destination!, created));
            }

            case StockChangeEffectKind.SplitMerged:
            {
                if (!await SetQuantityAsync(command, change.Source, cancellationToken)
                    || !await SetQuantityAsync(command, change.Destination!, cancellationToken))
                {
                    return null;
                }

                return Effect(change, Recorded(change.Source), Recorded(change.Destination!));
            }

            case StockChangeEffectKind.Merged:
            case StockChangeEffectKind.RenameMerged:
            {
                if (!await SetQuantityAsync(command, change.Destination!, cancellationToken)
                    || !await DeleteAsync(command, change.Source, cancellationToken))
                {
                    return null;
                }

                return Effect(change, Recorded(change.Source), Recorded(change.Destination!));
            }

            case StockChangeEffectKind.Renamed:
            {
                var newName = change.NewName!;
                var newNormalizedName = change.NewNormalizedName!;

                var renamed = await db.StockEntries
                    .Where(e => e.Id == change.Source.StockEntryId!.Value.Value && e.InventoryId == command.InventoryId.Value)
                    .ExecuteUpdateAsync(
                        setters => setters
                            .SetProperty(e => e.Name, newName)
                            .SetProperty(e => e.NormalizedName, newNormalizedName)
                            .SetProperty(e => e.ConcurrencyStamp, Guid.NewGuid()),
                        cancellationToken);

                return renamed == 1
                    ? Effect(change, Recorded(change.Source with { Name = newName }), null)
                    : null;
            }

            case StockChangeEffectKind.Forgotten:
            {
                return await DeleteAsync(command, change.Source, cancellationToken)
                    ? Effect(change, Recorded(change.Source), null)
                    : null;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(change), change.Effect, "Unhandled stock change effect.");
        }
    }

    /// <summary>
    /// Stages the insert of a Stock Entry a change creates, through the domain factory so persistence
    /// never sees a name or Note the domain would have refused, and returns its new identity.
    /// </summary>
    private StockEntryId StageCreate(StockChangeSetCommand command, ProposedEntryState state)
    {
        var entry = StockEntry.Create(
            command.InventoryId,
            state.UnitId,
            state.LocationId,
            state.Name,
            state.Note,
            state.ResultingQuantity,
            command.Now);

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

        return entry.Id;
    }

    private async Task<bool> SetQuantityAsync(
        StockChangeSetCommand command, ProposedEntryState state, CancellationToken cancellationToken)
    {
        var quantity = state.ResultingQuantity.Value;

        var updated = await db.StockEntries
            .Where(e => e.Id == state.StockEntryId!.Value.Value && e.InventoryId == command.InventoryId.Value)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(e => e.Quantity, quantity)
                    .SetProperty(e => e.ConcurrencyStamp, Guid.NewGuid()),
                cancellationToken);

        return updated == 1;
    }

    private async Task<bool> DeleteAsync(
        StockChangeSetCommand command, ProposedEntryState state, CancellationToken cancellationToken)
    {
        var deleted = await db.StockEntries
            .Where(e => e.Id == state.StockEntryId!.Value.Value && e.InventoryId == command.InventoryId.Value)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 1;
    }

    private static async Task<StockChangeSetStoreResult> RolledBackConflictAsync(
        IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken);

        return new StockChangeSetStoreResult(StockChangeSetStoreOutcome.Conflict, null);
    }

    /// <summary>
    /// Whether the exact Equivalent Stock a change meant to create now exists. Unlocated Stock is the
    /// absence of a Location, so it is asked for as such rather than compared to a null parameter,
    /// which relational NULL semantics would never match.
    /// </summary>
    private async Task<bool> EquivalentExistsAsync(
        InventoryId inventoryId, ExpectedEquivalentStockAbsence absence, CancellationToken cancellationToken)
    {
        var rows = db.StockEntries.AsNoTracking().Where(e =>
            e.InventoryId == inventoryId.Value
            && e.NormalizedName == absence.NormalizedName
            && e.UnitId == absence.UnitId.Value);

        rows = absence.LocationId is { } locationId
            ? rows.Where(e => e.LocationId == locationId.Value)
            : rows.Where(e => e.LocationId == null);

        return await rows.AnyAsync(cancellationToken);
    }

    private async Task<bool> AnyExpectedAbsenceFilledAsync(StockChangeSetCommand command, CancellationToken cancellationToken)
    {
        foreach (var absence in command.ExpectedAbsences)
        {
            if (await EquivalentExistsAsync(command.InventoryId, absence, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<RecordedStockChangeSet> ReadRecordedAsync(
        StockChangeSetOperationEntity header, CancellationToken cancellationToken)
    {
        var rows = await db.StockChangeSetEffects
            .AsNoTracking()
            .Where(e => e.OperationId == header.OperationId)
            .OrderBy(e => e.Order)
            .ToListAsync(cancellationToken);

        return new RecordedStockChangeSet(
            new StockOperationId(header.OperationId),
            header.ProposalId is { } proposalId ? new ProposalId(proposalId) : null,
            rows.Select(ToRecorded).ToList());
    }

    private static RecordedStockChangeEffect ToRecorded(StockChangeSetEffectEntity row)
    {
        if (!StockMutationKinds.TryParse(row.Kind, out var kind)
            || !Enum.TryParse<StockChangeEffectKind>(row.Effect, ignoreCase: false, out var effect))
        {
            throw new InvalidOperationException("A recorded change set carried an unreadable kind or effect.");
        }

        return new RecordedStockChangeEffect(
            row.Order,
            kind,
            effect,
            new RecordedEntryState(
                new StockEntryId(row.SourceStockEntryId),
                row.SourceName,
                row.SourceUnitCanonicalName,
                row.SourceLocationName,
                Quantity.Create(row.SourcePreviousQuantity),
                Quantity.Create(row.SourceResultingQuantity),
                row.SourceRetired),
            row.DestinationStockEntryId is { } destinationId
                ? new RecordedEntryState(
                    new StockEntryId(destinationId),
                    row.DestinationName!,
                    row.DestinationUnitCanonicalName!,
                    row.DestinationLocationName,
                    Quantity.Create(row.DestinationPreviousQuantity ?? 0m),
                    Quantity.Create(row.DestinationResultingQuantity ?? 0m),
                    Retired: false)
                : null,
            Quantity.Create(row.TransferredQuantity))
        {
            NewName = row.NewName,
        };
    }

    private static StockChangeSetEffectEntity ToEntity(StockOperationId operationId, RecordedStockChangeEffect effect) => new()
    {
        Id = Guid.NewGuid(),
        OperationId = operationId.Value,
        Order = effect.Order,
        Kind = StockMutationKinds.ToMachineText(effect.Kind),
        Effect = effect.Effect.ToString(),
        SourceStockEntryId = effect.Source.StockEntryId.Value,
        SourceName = effect.Source.Name,
        SourceUnitCanonicalName = effect.Source.UnitCanonicalName,
        SourceLocationName = effect.Source.LocationName,
        SourcePreviousQuantity = effect.Source.PreviousQuantity.Value,
        SourceResultingQuantity = effect.Source.ResultingQuantity.Value,
        SourceRetired = effect.Source.Retired,
        DestinationStockEntryId = effect.Destination?.StockEntryId.Value,
        DestinationName = effect.Destination?.Name,
        DestinationUnitCanonicalName = effect.Destination?.UnitCanonicalName,
        DestinationLocationName = effect.Destination?.LocationName,
        DestinationPreviousQuantity = effect.Destination?.PreviousQuantity.Value,
        DestinationResultingQuantity = effect.Destination?.ResultingQuantity.Value,
        TransferredQuantity = effect.TransferredQuantity.Value,
        NewName = effect.NewName,
    };

    private static RecordedStockChangeEffect Effect(
        ProposedChange change, RecordedEntryState source, RecordedEntryState? destination) =>
        new(change.Order, change.Kind, change.Effect, source, destination, change.TransferredQuantity) { NewName = change.NewName };

    /// <summary>
    /// The recorded form of one proposed state. The proposal's own states are exact - they are what
    /// the Participant reviewed - so nothing is re-read to build this; only a newly created entry
    /// contributes an identity the proposal could not have known.
    /// </summary>
    private static RecordedEntryState Recorded(ProposedEntryState state, StockEntryId? identity = null) => new(
        identity ?? state.StockEntryId ?? throw new InvalidOperationException("A recorded state must name a Stock Entry."),
        state.Name,
        state.UnitCanonicalName,
        state.LocationName,
        state.PreviousQuantity,
        state.ResultingQuantity,
        state.Retired);
}
