using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IImportProposalStore"/>. It honours exactly the contract the SQL store
/// must: one pending proposal per Participant and Inventory, a guarded settle only one caller can
/// win, and a raw upload that is discarded by every path out of Pending.
/// </summary>
public sealed class InMemoryImportProposalStore : IImportProposalStore
{
    private sealed record Row(ImportProposal Proposal, ImportProposalStatus Status, DateTimeOffset? SettledAt)
    {
        public ReadOnlyMemory<byte>? RawContent { get; init; }
    }

    private readonly Dictionary<ImportProposalId, Row> _rows = [];

    public Task<bool> StoreAsync(
        ImportProposal proposal, ReadOnlyMemory<byte> rawContent, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var superseded = false;

        foreach (var existing in FindPendingRows(proposal.ParticipantId, proposal.InventoryId))
        {
            _rows[existing.Proposal.Id] = existing with
            {
                Status = ImportProposalStatus.Superseded,
                SettledAt = now,
                RawContent = null,
            };
            superseded = true;
        }

        _rows[proposal.Id] = new Row(proposal, ImportProposalStatus.Pending, null) { RawContent = rawContent };

        return Task.FromResult(superseded);
    }

    public Task<ImportProposal?> FindPendingAsync(
        ParticipantId participantId, InventoryId inventoryId, CancellationToken cancellationToken) =>
        Task.FromResult(FindPendingRows(participantId, inventoryId).SingleOrDefault()?.Proposal);

    public Task<ReadOnlyMemory<byte>?> FindRawContentAsync(ImportProposalId proposalId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(proposalId, out var row) ? row.RawContent : null);

    public Task<bool> SettleAsync(
        ImportProposalId proposalId, ImportProposalStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_rows.TryGetValue(proposalId, out var row) || row.Status != ImportProposalStatus.Pending)
        {
            return Task.FromResult(false);
        }

        _rows[proposalId] = row with { Status = status, SettledAt = now, RawContent = null };

        return Task.FromResult(true);
    }

    public Task<ImportProposalStatus?> FindStatusAsync(ImportProposalId proposalId, CancellationToken cancellationToken) =>
        Task.FromResult(_rows.TryGetValue(proposalId, out var row) ? (ImportProposalStatus?)row.Status : null);

    public Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken)
    {
        var expiring = _rows.Values
            .Where(row => row.Status == ImportProposalStatus.Pending && row.Proposal.IsExpired(now))
            .Take(maxRows)
            .ToList();

        foreach (var row in expiring)
        {
            _rows[row.Proposal.Id] = row with
            {
                Status = ImportProposalStatus.Expired,
                SettledAt = now,
                RawContent = null,
            };
        }

        return Task.FromResult(expiring.Count);
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

    private List<Row> FindPendingRows(ParticipantId participantId, InventoryId inventoryId) =>
        [.. _rows.Values.Where(row =>
            row.Status == ImportProposalStatus.Pending
            && row.Proposal.BelongsTo(participantId, inventoryId))];
}
