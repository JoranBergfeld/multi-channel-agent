using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IImportExecutionStore"/>: the one transaction Initial Import rests
/// on.
///
/// One <see cref="ApplyAsync"/> call locks and verifies every reference the entries name, consumes
/// the proposal, re-asserts the empty state the import was decided against, creates every entry,
/// appends one minimal semantic audit fact, writes its ledger row, and discards the raw upload -
/// inside one explicit transaction. Any failure rolls the whole thing back, so a caller that sees
/// <see cref="ImportExecutionOutcome.Conflict"/> can rely on nothing at all having happened,
/// including the proposal still being pending and its file still being there.
///
/// Two things here are deliberate rather than incidental:
///
/// <list type="bullet">
/// <item><b>Serializable, and why it has to be.</b> The empty-state assertion is a question about an
/// <em>absence</em>, and an absence is a range. Under read-committed a Stock Entry could be inserted
/// just after the check and commit just before this transaction does, leaving an "initial" import
/// sitting on top of stock nobody reviewed. Serializable makes the check take a range lock, so the
/// two serialize and one of them plainly loses.</item>
/// <item><b>The shared lock order.</b> References, then proposal, then Stock - the same order
/// <see cref="AssignedReferenceLocks"/> documents and both shipped writers follow, so an import and a
/// Retire contend in one agreed sequence rather than deadlocking halfway through.</item>
/// </list>
///
/// Nothing is reparsed, re-resolved or re-merged: what commits is exactly the entries a Participant
/// reviewed, and the file they came from is already only a digest by the time this runs.
///
/// This store takes only the <see cref="MultiChannelAgentDbContext"/>. It consumes the proposal with
/// its own guarded update rather than through <see cref="IImportProposalStore"/>, because the
/// consumption has to happen inside this transaction - and taking that store as well would suggest
/// there were two ways to do it.
/// </summary>
public sealed class SqlImportExecutionStore(MultiChannelAgentDbContext db) : IImportExecutionStore
{
    /// <summary>SQL Server's "Transaction was deadlocked ... and has been chosen as the deadlock victim".</summary>
    private const int DeadlockVictimErrorNumber = 1205;

    private static readonly string PendingStatus = nameof(ImportProposalStatus.Pending);

    public async Task<RecordedImport?> FindRecordedAsync(
        InventoryId inventoryId, ImportOperationId operationId, CancellationToken cancellationToken)
    {
        var header = await db.ImportOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.OperationId == operationId.Value && o.InventoryId == inventoryId.Value, cancellationToken);

        if (header is null || !FileDigest.TryParse(header.FileDigest, out var digest))
        {
            return null;
        }

        return new RecordedImport(
            operationId,
            new ImportProposalId(header.ProposalId),
            new ParticipantId(header.ActorId),
            digest,
            header.CreatedEntryCount);
    }

    public async Task<ImportExecutionResult> ApplyAsync(ImportExecutionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Before anything is opened: this identity may already be in the ledger, in which case the
        // import ran and its facts are re-reported rather than applied a second time.
        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } already)
        {
            return new ImportExecutionResult(ImportExecutionOutcome.AlreadyApplied, already);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            // 1. Hold every Unit and Location these entries name, active-only, in the shared order:
            //    references first, then the proposal, then Stock. A preview may be ten minutes old, so a
            //    Retire committed since then must stop the import here, before any Stock exists.
            if (!await AssignedReferenceLocks.TryHoldActiveAsync(
                    db,
                    command.InventoryId,
                    command.Entries.Select(entry => entry.UnitId),
                    command.Entries.Select(entry => entry.LocationId).OfType<LocationId>(),
                    cancellationToken))
            {
                return await RolledBackAsync(command, transaction, cancellationToken);
            }

            // 2. Consume the proposal, guarded, so two confirmations can never both import.
            var consumed = await db.ImportProposals
                .Where(p => p.ProposalId == command.ConsumesProposalId.Value && p.Status == PendingStatus)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(p => p.Status, nameof(ImportProposalStatus.Confirmed))
                        .SetProperty(p => p.SettledAt, command.Now)
                        .SetProperty(p => p.SettledAtTicks, command.Now.UtcTicks),
                    cancellationToken);

            if (consumed != 1)
            {
                return await RolledBackAsync(command, transaction, cancellationToken);
            }

            // 3. The authoritative empty-state assertion: the Inventory still holds exactly the Stock the
            //    import was decided against, which for an Initial Import is none at all. The serializable
            //    range lock this read takes is what keeps that true until the entries below have
            //    committed.
            if (!await StillHoldsAsync(command, cancellationToken))
            {
                return await RolledBackAsync(command, transaction, cancellationToken);
            }

            // 4. Every entry, through the domain factory, so persistence never sees a name or Note the
            //    domain would have refused.
            foreach (var entry in command.Entries)
            {
                var stockEntry = StockEntry.Create(
                    command.InventoryId,
                    entry.UnitId,
                    entry.LocationId,
                    entry.Name,
                    entry.Note,
                    entry.Quantity,
                    command.Now);

                db.StockEntries.Add(new StockEntryEntity
                {
                    Id = stockEntry.Id.Value,
                    InventoryId = stockEntry.InventoryId.Value,
                    UnitId = stockEntry.UnitId.Value,
                    LocationId = stockEntry.LocationId?.Value,
                    Name = stockEntry.Name,
                    NormalizedName = stockEntry.NormalizedName,
                    Note = stockEntry.Note,
                    Quantity = stockEntry.Quantity.Value,
                    CreatedAt = stockEntry.CreatedAt,
                });
            }

            // 5. One ledger row and exactly one minimal semantic audit fact. The fact says an import
            //    happened here, by whom, and when - never what was in it.
            db.ImportOperations.Add(new ImportOperationEntity
            {
                OperationId = command.OperationId.Value,
                InventoryId = command.InventoryId.Value,
                ProposalId = command.ConsumesProposalId.Value,
                ActorId = command.ActorId.Value,
                FileDigest = command.FileDigest.Value,
                CreatedEntryCount = command.Entries.Count,
                AppliedAt = command.Now,
            });

            db.InventoryAudits.Add(InventoryAuditMapper.ToEntity(AuditFact.Create(
                AuditEventType.StockImported,
                AuditActorKind.Participant,
                command.ActorId.ToString(),
                command.InventoryId,
                subjectParticipantId: null,
                ImportFacts.CompletedOutcomeCode,
                command.Now)));

            // 6. The raw file goes with the import that used it.
            await db.ImportUploads
                .Where(u => u.ProposalId == command.ConsumesProposalId.Value)
                .ExecuteDeleteAsync(cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ImportExecutionResult(
                ImportExecutionOutcome.Applied,
                new RecordedImport(
                    command.OperationId,
                    command.ConsumesProposalId,
                    command.ActorId,
                    command.FileDigest,
                    command.Entries.Count));
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            // Both shapes are reachable and mean the same thing here: SaveChangesAsync reports a
            // failed insert as a DbUpdateException, while a guarded ExecuteUpdate or ExecuteDelete -
            // and a serializable deadlock victim taken before a single row is saved - raises the
            // provider's own exception instead.
            await db.AbandonAsync(transaction);

            if (await ClassifyFailedWriteAsync(command, exception, cancellationToken) is { } classified)
            {
                return classified;
            }

            throw;
        }
        catch
        {
            // Every other fault leaves exactly the same debris, and a cancellation between staging
            // five thousand entries and saving them is entirely reachable. The transaction would roll
            // back on dispose, but the ChangeTracker would not - and this DbContext serves a whole
            // batch of requests. Nothing is established here, so nothing is classified: the fault
            // propagates unchanged.
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// What a failed write may be reported as, or null when it may only be rethrown.
    ///
    /// The ledger is asked first, and is asked whatever the fault was: the competing writer may have
    /// been this very operation applied by another replica, and its row is the authoritative record of
    /// what happened. Converging on it re-reports this operation's own effect instead of claiming a
    /// conflict against ourselves.
    ///
    /// A deadlock victim is otherwise never laundered into a semantic answer. Nothing was applied and
    /// nothing was established, so it propagates as the transient thing it is and the request is
    /// retried - the same contract every other writer here keeps.
    ///
    /// Anything else is classified only on evidence. Equivalent Stock is unique in the database, so a
    /// competing writer that created one of these entries first makes the insert fail; that counts as
    /// a conflict exactly when the Inventory genuinely no longer holds the state this import was
    /// decided against. A failure without that evidence is a real fault and keeps propagating.
    /// </summary>
    private async Task<ImportExecutionResult?> ClassifyFailedWriteAsync(
        ImportExecutionCommand command, Exception exception, CancellationToken cancellationToken)
    {
        if (await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged)
        {
            return new ImportExecutionResult(ImportExecutionOutcome.AlreadyApplied, converged);
        }

        if (IsDeadlockVictim(exception) || await StillHoldsAsync(command, cancellationToken))
        {
            return null;
        }

        return new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
    }

    /// <summary>
    /// SQL Server's "Transaction (Process ID N) was deadlocked ... and has been chosen as the deadlock
    /// victim". Looked for through the whole chain, because EF Core wraps it.
    /// </summary>
    private static bool IsDeadlockVictim(Exception? exception) => exception switch
    {
        SqlException { Number: DeadlockVictimErrorNumber } => true,
        { InnerException: { } inner } => IsDeadlockVictim(inner),
        _ => false,
    };

    /// <summary>
    /// Whether the Inventory still holds exactly the Stock this import was decided against.
    ///
    /// The read is bounded to one row more than was expected, because that is all it takes to know
    /// the state moved: an Inventory somebody has since filled is never counted in full. Asking for
    /// the expected count rather than assuming zero keeps the assertion the command's own - the
    /// <see cref="EmptyStateVersion"/> a preview was decided against is re-asserted here, not merely
    /// alluded to.
    /// </summary>
    private async Task<bool> StillHoldsAsync(ImportExecutionCommand command, CancellationToken cancellationToken)
    {
        var expected = command.EmptyStateVersion.ExpectedStockEntryCount;

        var present = await db.StockEntries
            .Where(e => e.InventoryId == command.InventoryId.Value)
            .Take(expected + 1)
            .CountAsync(cancellationToken);

        return present == expected;
    }

    /// <summary>
    /// Ends a refused import: everything it staged and everything it changed goes back first, and only
    /// then is the answer decided.
    ///
    /// The ledger is consulted before <see cref="ImportExecutionOutcome.Conflict"/> is reported,
    /// because from inside this transaction a refusal and a replay look identical. Two confirmations
    /// of one import carry the <em>same</em> derived operation identity - the identity comes from the
    /// proposal, not from the request - so the one that loses the guarded consume above blocks on the
    /// winner's row lock, resumes after the winner has committed, and finds the proposal spent. Its
    /// import did happen; telling its Participant that nothing did would simply be false, and the
    /// ledger row is the authoritative record of it.
    /// </summary>
    private async Task<ImportExecutionResult> RolledBackAsync(
        ImportExecutionCommand command, IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        await db.AbandonAsync(transaction);

        return await FindRecordedAsync(command.InventoryId, command.OperationId, cancellationToken) is { } converged
            ? new ImportExecutionResult(ImportExecutionOutcome.AlreadyApplied, converged)
            : new ImportExecutionResult(ImportExecutionOutcome.Conflict, null);
    }
}
