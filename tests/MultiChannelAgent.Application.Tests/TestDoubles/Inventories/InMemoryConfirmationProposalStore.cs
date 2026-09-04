using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// In-memory <see cref="IConfirmationProposalStore"/> holding exactly the invariants the SQL store
/// enforces relationally: one Pending proposal per Participant and ChannelConversation, replacement
/// that supersedes, and a settle that only the first caller wins.
/// </summary>
public sealed class InMemoryConfirmationProposalStore : IConfirmationProposalStore
{
    private sealed record Row(ConfirmationProposal Proposal, ProposalStatus Status, DateTimeOffset? SettledAt);

    private readonly Dictionary<ProposalId, Row> _rows = [];

    public Task<ConfirmationProposal?> FindPendingAsync(
        ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken) =>
        Task.FromResult(FindPendingRow(participantId, channelConversationId)?.Proposal);

    public Task<StoredProposalReplacement> StoreAsync(
        ConfirmationProposal proposal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = FindPendingRow(proposal.ParticipantId, proposal.ChannelConversationId);
        if (existing is not null)
        {
            _rows[existing.Proposal.Id] = existing with { Status = ProposalStatus.Superseded, SettledAt = now };
        }

        _rows[proposal.Id] = new Row(proposal, ProposalStatus.Pending, null);

        return Task.FromResult(new StoredProposalReplacement(existing is not null));
    }

    public Task<bool> SettleAsync(
        ProposalId proposalId, ProposalStatus status, DateTimeOffset settledAt, CancellationToken cancellationToken)
    {
        if (!_rows.TryGetValue(proposalId, out var row) || row.Status != ProposalStatus.Pending)
        {
            return Task.FromResult(false);
        }

        _rows[proposalId] = row with { Status = status, SettledAt = settledAt };
        return Task.FromResult(true);
    }

    public Task<ProposalStatus?> FindStatusAsync(ProposalId proposalId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(proposalId, out var row) ? row.Status : (ProposalStatus?)null);

    public async Task<int> InvalidatePendingAsync(
        ParticipantId participantId,
        string channelConversationId,
        ProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = FindPendingRow(participantId, channelConversationId);

        return pending is not null && await SettleAsync(pending.Proposal.Id, status, now, cancellationToken) ? 1 : 0;
    }

    public Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var expired = _rows.Values
            .Where(row => row.Status == ProposalStatus.Pending && row.Proposal.IsExpired(now))
            .Take(maxRows)
            .ToList();

        foreach (var row in expired)
        {
            _rows[row.Proposal.Id] = row with { Status = ProposalStatus.Expired, SettledAt = now };
        }

        return Task.FromResult(expired.Count);
    }

    public Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        var deletable = _rows.Values
            .Where(row => row.SettledAt is { } settledAt && settledAt <= cutoff)
            .Take(maxRows)
            .ToList();

        foreach (var row in deletable)
        {
            _rows.Remove(row.Proposal.Id);
        }

        return Task.FromResult(deletable.Count);
    }

    /// <summary>
    /// Settles every pending proposal that references this Unit or Location, as retiring it must -
    /// including a stock proposal, which could otherwise create or move stock at a reference that no
    /// longer exists.
    /// </summary>
    public async Task<int> InvalidateReferencingAsync(
        InventoryId inventoryId,
        ReferenceKind kind,
        Guid referenceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var affected = _rows.Values
            .Where(row => row.Status == ProposalStatus.Pending
                && row.Proposal.InventoryId == inventoryId
                && (kind == ReferenceKind.Unit
                    ? row.Proposal.ReferencedUnitIds.Contains(new UnitId(referenceId))
                    : row.Proposal.ReferencedLocationIds.Contains(new LocationId(referenceId))))
            .Select(row => row.Proposal.Id)
            .ToList();

        var settled = 0;
        foreach (var proposalId in affected)
        {
            if (await SettleAsync(proposalId, ProposalStatus.Conflicted, now, cancellationToken))
            {
                settled++;
            }
        }

        return settled;
    }

    private Row? FindPendingRow(ParticipantId participantId, string channelConversationId) => _rows.Values.SingleOrDefault(row =>
        row.Status == ProposalStatus.Pending
        && row.Proposal.ParticipantId == participantId
        && string.Equals(row.Proposal.ChannelConversationId, channelConversationId, StringComparison.Ordinal));
}
