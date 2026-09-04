using System.Data;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IImportProposalStore"/>.
///
/// Storing supersedes any import this Participant already had pending for this Inventory, in one
/// transaction, so the filtered unique index can never be raced into a violation and the superseded
/// file is discarded with it. Every path out of <c>Pending</c> deletes the raw upload, which is what
/// makes "the raw CSV is discarded after completion or expiry" a durable fact rather than a promise.
/// </summary>
public sealed class SqlImportProposalStore(MultiChannelAgentDbContext db) : IImportProposalStore
{
    private static readonly string PendingStatus = nameof(ImportProposalStatus.Pending);

    public async Task<bool> StoreAsync(
        ImportProposal proposal, ReadOnlyMemory<byte> rawContent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        try
        {
            // Superseding first means the filtered unique index is free by the time the insert lands.
            var superseded = await SupersedePendingAsync(proposal, now, cancellationToken);

            db.ImportProposals.Add(ImportProposalMapper.ToEntity(proposal));
            db.ImportUploads.Add(new ImportUploadEntity
            {
                // Copied, never referenced: the caller's buffer may be pooled or reused the moment
                // this returns, and what is retained has to be exactly the bytes that were digested.
                Content = rawContent.ToArray(),
                ProposalId = proposal.Id.Value,
                CreatedAt = now,
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return superseded > 0;
        }
        catch
        {
            // The insert is staged before it is saved, and this DbContext serves a whole batch of
            // Turns. An entity left Added by a failed store would otherwise be committed later by an
            // unrelated Turn's SaveChangesAsync - and what it would commit is a *pending* import
            // proposal, which is precisely the row a later confirmation executes.
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// Supersedes the current row with one guarded UPDATE before reading any identities. Under the
    /// serializable Store transaction, that statement holds the Participant+Inventory pending-key
    /// range through the replacement insert. Two validations therefore serialize at this point:
    /// neither can observe an empty range, skip the update, and then race the other's insert into the
    /// filtered unique index.
    /// </summary>
    private async Task<int> SupersedePendingAsync(
        ImportProposal replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settled = await db.ImportProposals
            .Where(proposal =>
                proposal.ParticipantId == replacement.ParticipantId.Value
                && proposal.InventoryId == replacement.InventoryId.Value
                && proposal.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(proposal => proposal.Status, nameof(ImportProposalStatus.Superseded))
                    .SetProperty(proposal => proposal.SettledAt, now)
                    .SetProperty(proposal => proposal.SettledAtTicks, now.UtcTicks),
                cancellationToken);

        if (settled == 0)
        {
            return 0;
        }

        var supersededIds = await db.ImportProposals
            .AsNoTracking()
            .Where(proposal =>
                proposal.ParticipantId == replacement.ParticipantId.Value
                && proposal.InventoryId == replacement.InventoryId.Value
                && proposal.Status == nameof(ImportProposalStatus.Superseded)
                && proposal.SettledAtTicks == now.UtcTicks)
            .Select(proposal => proposal.ProposalId)
            .ToListAsync(cancellationToken);

        await db.ImportUploads
            .Where(upload => supersededIds.Contains(upload.ProposalId))
            .ExecuteDeleteAsync(cancellationToken);

        return settled;
    }

    public async Task<ImportProposal?> FindPendingAsync(
        ParticipantId participantId, InventoryId inventoryId, CancellationToken cancellationToken)
    {
        var row = await db.ImportProposals
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.ParticipantId == participantId.Value
                    && p.InventoryId == inventoryId.Value
                    && p.Status == PendingStatus,
                cancellationToken);

        return row is null ? null : ImportProposalMapper.ToDomain(row);
    }

    public async Task<ReadOnlyMemory<byte>?> FindRawContentAsync(
        ImportProposalId proposalId, CancellationToken cancellationToken)
    {
        var content = await db.ImportUploads
            .AsNoTracking()
            .Where(u => u.ProposalId == proposalId.Value)
            .Select(u => u.Content)
            .FirstOrDefaultAsync(cancellationToken);

        // The cast is load-bearing. byte[] converts implicitly to ReadOnlyMemory<byte>, so without it
        // the conditional's natural type is ReadOnlyMemory<byte>, the null branch becomes an *empty*
        // buffer, and "the file is gone" would read as "the file was empty" - which is precisely the
        // difference between a discarded upload and one this store never had.
        return content is null ? null : (ReadOnlyMemory<byte>?)content.AsMemory();
    }

    public async Task<bool> SettleAsync(
        ImportProposalId proposalId, ImportProposalStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var settled = await SettlePendingAsync(
                db.ImportProposals.Where(p => p.ProposalId == proposalId.Value), status, now, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return settled == 1;
        }
        catch
        {
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    public async Task<ImportProposalStatus?> FindStatusAsync(ImportProposalId proposalId, CancellationToken cancellationToken)
    {
        var status = await db.ImportProposals
            .AsNoTracking()
            .Where(p => p.ProposalId == proposalId.Value)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is null ? null : Enum.Parse<ImportProposalStatus>(status);
    }

    public async Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var ticks = now.UtcTicks;

        // The bounded set is selected first so one pass can never turn into an unbounded update, and
        // so the oldest expiries are always the ones taken.
        var expiring = await db.ImportProposals
            .AsNoTracking()
            .Where(p => p.Status == PendingStatus && p.ExpiresAtTicks <= ticks)
            .OrderBy(p => p.ExpiresAtTicks)
            .Take(maxRows)
            .Select(p => p.ProposalId)
            .ToListAsync(cancellationToken);

        if (expiring.Count == 0)
        {
            return 0;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var settled = await SettlePendingAsync(
                db.ImportProposals.Where(p => expiring.Contains(p.ProposalId)),
                ImportProposalStatus.Expired,
                now,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return settled;
        }
        catch
        {
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    public async Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var ticks = cutoff.UtcTicks;

        var deletable = await db.ImportProposals
            .AsNoTracking()
            .Where(p => p.SettledAtTicks != null && p.SettledAtTicks <= ticks)
            .OrderBy(p => p.SettledAtTicks)
            .Take(maxRows)
            .Select(p => p.ProposalId)
            .ToListAsync(cancellationToken);

        return deletable.Count == 0
            ? 0

            // The upload cascades with it, so a settled proposal never leaves a file behind even if
            // one somehow survived its settle.
            : await db.ImportProposals.Where(p => deletable.Contains(p.ProposalId)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Moves matching rows out of Pending, guarded, and deletes their uploads in the same transaction.
    /// The guard is what makes a settle single-use: two callers racing one proposal are resolved by
    /// the database, and the loser is told it lost rather than guessing.
    /// </summary>
    private async Task<int> SettlePendingAsync(
        IQueryable<ImportProposalEntity> rows,
        ImportProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = rows.Where(p => p.Status == PendingStatus);

        var ids = await pending.AsNoTracking().Select(p => p.ProposalId).ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return 0;
        }

        var settled = await pending.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(p => p.Status, status.ToString())
                .SetProperty(p => p.SettledAt, now)
                .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
            cancellationToken);

        // The raw file goes with the settle, not with a later sweep: "discarded after completion or
        // expiry" means at completion, not eventually.
        await db.ImportUploads.Where(u => ids.Contains(u.ProposalId)).ExecuteDeleteAsync(cancellationToken);

        return settled;
    }
}
