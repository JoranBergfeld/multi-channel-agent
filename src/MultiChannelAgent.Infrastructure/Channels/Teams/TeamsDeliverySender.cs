using System.Text.Json;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Channels.Teams;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Infrastructure.Channels.Teams;

/// <summary>Delivers channel-neutral reply payloads back to the original Teams personal chat.</summary>
public sealed class TeamsDeliverySender(
    IChannelAdapter channelAdapter,
    IConnections connections,
    IInboxStore inboxStore,
    ITeamsConversationReferenceStore conversationReferenceStore,
    ILogger<TeamsDeliverySender> logger) : IDeliverySender
{
    public async Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        var originTurn = await inboxStore.FindByTurnIdAsync(delivery.TurnId, cancellationToken);
        if (originTurn is null)
        {
            logger.LogWarning("No origin Teams Turn found for delivery {TurnId}; retrying.", delivery.TurnId);
            return false;
        }

        var serializedReference = await conversationReferenceStore.FindAsync(originTurn.ChannelConversationId.Value, cancellationToken);
        if (string.IsNullOrWhiteSpace(serializedReference))
        {
            logger.LogWarning(
                "No stored Teams conversation reference found for conversation {ChannelConversationId}; retrying.",
                originTurn.ChannelConversationId.Value);
            return false;
        }

        try
        {
            var conversationReference = JsonSerializer.Deserialize<Microsoft.Agents.Core.Models.ConversationReference>(serializedReference);
            var agentId = connections.GetDefaultConnection().ConnectionSettings.ClientId;
            if (conversationReference is null || string.IsNullOrWhiteSpace(agentId))
            {
                logger.LogWarning(
                    "Teams delivery for conversation {ChannelConversationId} cannot continue without a valid reference and agent id.",
                    originTurn.ChannelConversationId.Value);
                return false;
            }

            var identity = AgentClaims.CreateIdentity(agentId, anonymous: false, appId: agentId);
            await channelAdapter.ContinueConversationAsync(
                identity,
                conversationReference,
                agentId,
                (turnContext, ct) => turnContext.SendActivityAsync(delivery.Payload, string.Empty, string.Empty, ct),
                cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send Teams reply for delivery {TurnId}.", delivery.TurnId);
            return false;
        }
    }
}
