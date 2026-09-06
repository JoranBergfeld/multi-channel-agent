namespace MultiChannelAgent.Application.Channels.Teams;

/// <summary>
/// Durable store for the serialized conversation reference Teams requires to proactively resume a
/// 1:1 chat later, since reply delivery happens on a background worker, never inside the inbound
/// activity request.
/// </summary>
public interface ITeamsConversationReferenceStore
{
    Task SaveAsync(string channelConversationId, string serializedReference, CancellationToken cancellationToken);

    Task<string?> FindAsync(string channelConversationId, CancellationToken cancellationToken);
}
