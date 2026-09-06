using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Channels.Email;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Infrastructure.Channels.Email;

/// <summary>Delivers channel-neutral reply payloads back to the original Microsoft Graph email thread.</summary>
public sealed class GraphEmailDeliverySender(
    IGraphMailboxClient graphMailboxClient,
    IInboxStore inboxStore,
    ILogger<GraphEmailDeliverySender> logger) : IDeliverySender
{
    public async Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        var originTurn = await inboxStore.FindByTurnIdAsync(delivery.TurnId, cancellationToken);
        if (originTurn?.NativeMessageId is not { } nativeMessageId)
        {
            logger.LogWarning("No origin email Turn/native message id found for delivery {TurnId}; retrying.", delivery.TurnId);
            return false;
        }

        try
        {
            await graphMailboxClient.SendReplyAsync(nativeMessageId, delivery.Payload, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send email reply for delivery {TurnId}.", delivery.TurnId);
            return false;
        }
    }
}
