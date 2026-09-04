using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// Default <see cref="IDeliverySender"/> for this tracer scenario: no real channel adapters exist
/// yet, so Delivery is recorded via structured logging and always reports success. Real channel
/// senders (web, Teams, email) replace this per-channel in later tickets.
///
/// It deliberately logs the shape of an answer rather than the answer. A Delivery payload carries
/// Stock Entry names and Quantities - and, for a confirmation, a live single-use token - none of
/// which belongs in a diagnostic sink that outlives the answer and is readable by people who were
/// never authorized for that Inventory.
/// </summary>
public sealed class LoggingDeliverySender(ILogger<LoggingDeliverySender> logger) : IDeliverySender
{
    public Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Delivering Turn {TurnId} to channel {Channel} ({PayloadLength} characters).",
            delivery.TurnId,
            delivery.Channel,
            delivery.Payload.Length);

        return Task.FromResult(true);
    }
}
