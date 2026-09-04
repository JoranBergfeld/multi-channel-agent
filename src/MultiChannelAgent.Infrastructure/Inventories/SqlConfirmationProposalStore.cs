using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IConfirmationProposalStore"/>.
///
/// Two things here are load-bearing. First, <see cref="StoreAsync"/> supersedes and inserts inside
/// one transaction, so a conversation is never briefly holding two pending proposals - or none - and
/// a confirmation arriving mid-replacement can only ever mean one of them. Second, every status
/// change is a single guarded UPDATE with <c>Status = 'Pending'</c> in its predicate, so "single
/// use" is decided by the database's own row lock rather than by a read-then-write this process
/// could lose.
/// </summary>
public sealed class SqlConfirmationProposalStore(MultiChannelAgentDbContext db) : IConfirmationProposalStore
{
    private static readonly string PendingStatus = nameof(ProposalStatus.Pending);

    public async Task<ConfirmationProposal?> FindPendingAsync(
        ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken)
    {
        var entity = await db.ConfirmationProposals
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.ParticipantId == participantId.Value
                    && p.ChannelConversationId == channelConversationId
                    && p.Status == PendingStatus,
                cancellationToken);

        return entity is null ? null : ConfirmationProposalMapper.ToDomain(entity);
    }

    public async Task<StoredProposalReplacement> StoreAsync(
        ConfirmationProposal proposal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var superseded = await db.ConfirmationProposals
                .Where(p => p.ParticipantId == proposal.ParticipantId.Value
                    && p.ChannelConversationId == proposal.ChannelConversationId
                    && p.Status == PendingStatus)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(p => p.Status, nameof(ProposalStatus.Superseded))
                        .SetProperty(p => p.SettledAt, now)
                        .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
                    cancellationToken);

            db.ConfirmationProposals.Add(ConfirmationProposalMapper.ToEntity(proposal));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new StoredProposalReplacement(superseded > 0);
        }
        catch
        {
            // The insert is staged before it is saved, and this DbContext serves a whole batch of
            // Turns. A failed supersede, a losing race on one of this table's unique indexes, a
            // deadlock, or a cancellation would otherwise leave an Added *pending* proposal waiting
            // for an unrelated Turn's SaveChangesAsync to commit it - and a pending proposal is
            // precisely the row a later "confirm" would execute.
            //
            // Nothing is classified here. Storing a proposal has no conflict outcome to report: it
            // either happened or it did not, so the fault propagates exactly as it arrived.
            await db.AbandonAsync(transaction);
            throw;
        }
    }

    public async Task<bool> SettleAsync(
        ProposalId proposalId, ProposalStatus status, DateTimeOffset settledAt, CancellationToken cancellationToken)
    {
        // Guarded on Status: the second caller updates zero rows and is told so, which is exactly how
        // a proposal is used at most once.
        var settled = await db.ConfirmationProposals
            .Where(p => p.ProposalId == proposalId.Value && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(p => p.Status, status.ToString())
                    .SetProperty(p => p.SettledAt, settledAt)
                    .SetProperty(p => p.SettledAtTicks, settledAt.UtcTicks),
                cancellationToken);

        return settled == 1;
    }

    public async Task<ProposalStatus?> FindStatusAsync(ProposalId proposalId, CancellationToken cancellationToken)
    {
        var status = await db.ConfirmationProposals
            .AsNoTracking()
            .Where(p => p.ProposalId == proposalId.Value)
            .Select(p => p.Status)
            .FirstOrDefaultAsync(cancellationToken);

        return status is null ? null : Enum.Parse<ProposalStatus>(status);
    }

    public async Task<int> InvalidatePendingAsync(
        ParticipantId participantId,
        string channelConversationId,
        ProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await db.ConfirmationProposals
            .Where(p => p.ParticipantId == participantId.Value
                && p.ChannelConversationId == channelConversationId
                && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(p => p.Status, status.ToString())
                    .SetProperty(p => p.SettledAt, now)
                    .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
                cancellationToken);

    public async Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var nowTicks = now.UtcTicks;

        // The bounded set is selected first so one pass can never turn into an unbounded update, and
        // the update itself runs as a single set-based statement rather than by loading proposals.
        // This is also the portable shape: ordering and bounding inside ExecuteUpdate is not
        // translatable on every provider.
        var expiringIds = await db.ConfirmationProposals
            .AsNoTracking()
            .Where(p => p.Status == PendingStatus && p.ExpiresAtTicks <= nowTicks)
            .OrderBy(p => p.ExpiresAtTicks)
            .Take(maxRows)
            .Select(p => p.ProposalId)
            .ToListAsync(cancellationToken);

        if (expiringIds.Count == 0)
        {
            return 0;
        }

        return await db.ConfirmationProposals
            .Where(p => expiringIds.Contains(p.ProposalId) && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(p => p.Status, nameof(ProposalStatus.Expired))
                    .SetProperty(p => p.SettledAt, now)
                    .SetProperty(p => p.SettledAtTicks, now.UtcTicks),
                cancellationToken);
    }

    public async Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var cutoffTicks = cutoff.UtcTicks;

        var deletableIds = await db.ConfirmationProposals
            .AsNoTracking()
            .Where(p => p.SettledAtTicks != null && p.SettledAtTicks <= cutoffTicks)
            .OrderBy(p => p.SettledAtTicks)
            .Take(maxRows)
            .Select(p => p.ProposalId)
            .ToListAsync(cancellationToken);

        if (deletableIds.Count == 0)
        {
            return 0;
        }

        return await db.ConfirmationProposals
            .Where(p => deletableIds.Contains(p.ProposalId))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
