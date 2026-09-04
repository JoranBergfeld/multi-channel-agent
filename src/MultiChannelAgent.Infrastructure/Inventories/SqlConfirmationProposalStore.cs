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

        var superseded = await db.ConfirmationProposals
            .Where(p => p.ParticipantId == proposal.ParticipantId.Value
                && p.ChannelConversationId == proposal.ChannelConversationId
                && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(p => p.Status, nameof(ProposalStatus.Superseded))
                    .SetProperty(p => p.SettledAt, now),
                cancellationToken);

        db.ConfirmationProposals.Add(ConfirmationProposalMapper.ToEntity(proposal));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new StoredProposalReplacement(superseded > 0);
    }

    public async Task<bool> SettleAsync(
        ProposalId proposalId, ProposalStatus status, DateTimeOffset settledAt, CancellationToken cancellationToken)
    {
        // Guarded on Status: the second caller updates zero rows and is told so, which is exactly how
        // a proposal is used at most once.
        var settled = await db.ConfirmationProposals
            .Where(p => p.ProposalId == proposalId.Value && p.Status == PendingStatus)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(p => p.Status, status.ToString()).SetProperty(p => p.SettledAt, settledAt),
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
                setters => setters.SetProperty(p => p.Status, status.ToString()).SetProperty(p => p.SettledAt, now),
                cancellationToken);

    public async Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var expiring = db.ConfirmationProposals
            .Where(p => p.Status == PendingStatus && p.ExpiresAt <= now)
            .OrderBy(p => p.ExpiresAt)
            .Take(maxRows);

        return await expiring.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(p => p.Status, nameof(ProposalStatus.Expired))
                .SetProperty(p => p.SettledAt, now),
            cancellationToken);
    }

    public async Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var deletable = db.ConfirmationProposals
            .Where(p => p.SettledAt != null && p.SettledAt <= cutoff)
            .OrderBy(p => p.SettledAt)
            .Take(maxRows);

        return await deletable.ExecuteDeleteAsync(cancellationToken);
    }
}
