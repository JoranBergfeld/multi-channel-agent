using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>One requested Delivery's status as exposed at the application boundary.</summary>
public sealed record DeliveryView(Guid DeliveryId, string Channel, string Status, int Attempts);

/// <summary>
/// The recorded Outcome of a Turn, plus its requested Deliveries, as exposed at the application
/// boundary. Never present until processing has produced a terminal Outcome. <see cref="Payload"/> is
/// the parsed form of <see cref="Domain.Turns.Outcome.Payload"/> (a typed semantic result such as a
/// Stock List page or Find candidates) when the Outcome carries one, null for the pre-existing echo
/// tracer shape.
/// </summary>
public sealed record TurnOutcomeView(
    Guid TurnId,
    string Status,
    string Category,
    string Code,
    string Summary,
    JsonElement? Payload,
    IReadOnlyList<DeliveryView> Deliveries);

/// <summary>
/// Reads back the terminal Outcome and requested Deliveries recorded for a Turn. This is the
/// application-boundary read side that channel adapters and the web client use to observe results.
/// A caller may only read a Turn's own recorded result: <see cref="GetAsync"/> returns null - the same
/// shape as "not found" - for a Turn that exists but belongs to a different Participant, so a caller
/// can never learn that some other Participant's Turn exists.
/// </summary>
public sealed class TurnOutcomeReader(IInboxStore inboxStore, IOutcomeStore outcomeStore, IDeliveryStore deliveryStore)
{
    public async Task<TurnOutcomeView?> GetAsync(TurnId turnId, ParticipantId requestingParticipantId, CancellationToken cancellationToken)
    {
        var turn = await inboxStore.FindByTurnIdAsync(turnId, cancellationToken);
        if (turn is null || turn.ParticipantId != requestingParticipantId)
        {
            return null;
        }

        var outcome = await outcomeStore.FindAsync(turnId, cancellationToken);
        if (outcome is null)
        {
            return null;
        }

        var deliveries = await deliveryStore.FindByTurnIdAsync(turnId, cancellationToken);

        return new TurnOutcomeView(
            turnId.Value,
            outcome.Status.ToString().ToLowerInvariant(),
            outcome.Category.ToMachineText(),
            outcome.Code,
            outcome.Summary,
            outcome.Payload is null ? null : JsonSerializer.Deserialize<JsonElement>(outcome.Payload),
            deliveries
                .Select(d => new DeliveryView(d.DeliveryId, d.Channel, d.Status.ToString().ToLowerInvariant(), d.Attempts))
                .ToList());
    }
}
