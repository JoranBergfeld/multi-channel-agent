using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Records a denied-access audit fact and, when the denial happened while checking one Participant's
/// Active Inventory selection for one ChannelConversation, clears that stale selection - atomically,
/// in one transaction, so a denial is never recorded without also clearing the access it denies, and
/// vice versa.
/// </summary>
public interface IInventoryAuthorizationAuditStore
{
    Task RecordDenialAsync(
        AuditFact fact,
        ParticipantId? clearSelectionParticipantId,
        string? clearSelectionChannelConversationId,
        CancellationToken cancellationToken);
}
