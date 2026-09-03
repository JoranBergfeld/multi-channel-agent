using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// Default <see cref="IDeliverySender"/> for this tracer scenario: no real channel adapters exist
/// yet, so Delivery is recorded via structured logging and always reports success. Real channel
/// senders (web, Teams, email) replace this per-channel in later tickets.
/// </summary>
public sealed class LoggingDeliverySender(ILogger<LoggingDeliverySender> logger) : IDeliverySender
{
    public Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Delivering Turn {TurnId} to channel {Channel}: {Payload}",
            delivery.TurnId,
            delivery.Channel,
            delivery.Payload);

        return Task.FromResult(true);
    }
}
