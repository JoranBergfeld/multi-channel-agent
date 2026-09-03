using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Minimal in-memory <see cref="IInventoryAuthorizationAuditStore"/> for Application-layer unit
/// tests: records every denial fact and every selection-clear request it receives so tests can assert
/// on them directly, without a real database.
/// </summary>
public sealed class InMemoryInventoryAuthorizationAuditStore(InMemoryActiveInventorySelectionStore? selectionStore = null)
    : IInventoryAuthorizationAuditStore
{
    private readonly List<AuditFact> _recordedFacts = [];

    public IReadOnlyList<AuditFact> RecordedFacts => _recordedFacts;

    public Task RecordDenialAsync(
        AuditFact fact,
        ParticipantId? clearSelectionParticipantId,
        string? clearSelectionChannelConversationId,
        CancellationToken cancellationToken)
    {
        _recordedFacts.Add(fact);

        if (selectionStore is not null && clearSelectionParticipantId is not null && clearSelectionChannelConversationId is not null)
        {
            selectionStore.ClearAsync(clearSelectionParticipantId.Value, clearSelectionChannelConversationId, cancellationToken);
        }

        return Task.CompletedTask;
    }
}
