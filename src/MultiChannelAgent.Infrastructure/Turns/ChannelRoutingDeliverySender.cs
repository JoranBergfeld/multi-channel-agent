using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// Routes a <see cref="Delivery"/> to the sender for the channel the originating Turn arrived on.
/// <see cref="Delivery.Channel"/> is never this channel - it is the tool dispatcher's fixed response
/// label - so routing always resolves the origin Turn via <see cref="IInboxStore.FindByTurnIdAsync"/>
/// and dispatches on its <see cref="InboundTurn.Channel"/> instead.
/// </summary>
public sealed class ChannelRoutingDeliverySender(
    IInboxStore inboxStore,
    IServiceProvider serviceProvider,
    ILogger<ChannelRoutingDeliverySender> logger) : IDeliverySender
{
    public async Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        var originTurn = await inboxStore.FindByTurnIdAsync(delivery.TurnId, cancellationToken);
        if (originTurn?.Channel is not { } channel)
        {
            logger.LogWarning("No origin Turn/channel found for delivery {TurnId}; dropping.", delivery.TurnId);
            return false;
        }

        var sender = serviceProvider.GetKeyedService<IDeliverySender>(channel);
        if (sender is null)
        {
            logger.LogWarning("No delivery sender registered for channel {Channel}; dropping.", channel);
            return false;
        }

        return await sender.TrySendAsync(delivery, cancellationToken);
    }
}
