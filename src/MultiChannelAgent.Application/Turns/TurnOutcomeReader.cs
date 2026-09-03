using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>One requested Delivery's status as exposed at the application boundary.</summary>
public sealed record DeliveryView(Guid DeliveryId, string Channel, string Status, int Attempts);

/// <summary>
/// The recorded Outcome of a Turn, plus its requested Deliveries, as exposed at the application
/// boundary. Never present until processing has produced a terminal Outcome.
/// </summary>
public sealed record TurnOutcomeView(
    TurnId TurnId,
    string Status,
    string Code,
    string Summary,
    IReadOnlyList<DeliveryView> Deliveries);

/// <summary>
/// Reads back the terminal Outcome and requested Deliveries recorded for a Turn. This is the
/// application-boundary read side that channel adapters and the web client use to observe results.
/// </summary>
public sealed class TurnOutcomeReader(IOutcomeStore outcomeStore, IDeliveryStore deliveryStore)
{
    public async Task<TurnOutcomeView?> GetAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        var outcome = await outcomeStore.FindAsync(turnId, cancellationToken);
        if (outcome is null)
        {
            return null;
        }

        var deliveries = await deliveryStore.FindByTurnIdAsync(turnId, cancellationToken);

        return new TurnOutcomeView(
            turnId,
            outcome.Status.ToString().ToLowerInvariant(),
            outcome.Code,
            outcome.Summary,
            deliveries
                .Select(d => new DeliveryView(d.DeliveryId, d.Channel, d.Status.ToString().ToLowerInvariant(), d.Attempts))
                .ToList());
    }
}
