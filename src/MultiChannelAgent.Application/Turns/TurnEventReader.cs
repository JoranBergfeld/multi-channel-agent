using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

public sealed record TurnStreamEvent(long Sequence, string Name, string Data);

public sealed record TurnEventPage(IReadOnlyList<TurnStreamEvent> Events, bool ReachedTerminal);

public sealed record TurnAcceptedData(Guid TurnId, DateTimeOffset ReceivedAt);

public sealed record TurnProcessingData(Guid TurnId, DateTimeOffset StartedAt);

public sealed record TurnResponsePartData(
    Guid TurnId,
    int Order,
    string Kind,
    string? Text,
    JsonElement? Payload);

public sealed record TurnStreamOutcomeData(
    Guid TurnId,
    string Status,
    string Category,
    string Code,
    string Summary,
    IReadOnlyList<DeliveryView> Deliveries);

public sealed class TurnEventReader(
    IInboxStore inboxStore,
    ITurnProgressEventStore progressEventStore,
    IOutcomeStore outcomeStore,
    IDeliveryStore deliveryStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<TurnEventPage?> ReadAfterAsync(
        TurnId turnId,
        ParticipantId requestingParticipantId,
        long after,
        CancellationToken cancellationToken)
    {
        var turn = await inboxStore.FindByTurnIdAsync(turnId, cancellationToken);
        if (turn is null || turn.ParticipantId != requestingParticipantId)
        {
            return null;
        }

        var events = new List<TurnStreamEvent>();
        if (after < TurnEventSequence.Accepted)
        {
            events.Add(Project(
                TurnEventSequence.Accepted,
                TurnEventKind.Accepted,
                new TurnAcceptedData(turnId.Value, turn.ReceivedAt)));
        }

        if (after < TurnEventSequence.Processing)
        {
            var retainedProgress = await progressEventStore.ReadAsync(turnId, cancellationToken);
            var processing = retainedProgress.FirstOrDefault(progressEvent =>
                progressEvent.Sequence == TurnEventSequence.Processing
                && progressEvent.Kind == TurnEventKind.Processing);
            if (processing is not null)
            {
                events.Add(Project(
                    TurnEventSequence.Processing,
                    TurnEventKind.Processing,
                    new TurnProcessingData(turnId.Value, processing.OccurredAt)));
            }
        }

        var outcome = await outcomeStore.FindAsync(turnId, cancellationToken);
        if (outcome is null)
        {
            return new TurnEventPage(events, ReachedTerminal: false);
        }

        AddAfter(
            events,
            after,
            TurnEventSequence.ForPart(1),
            TurnEventKind.Part,
            new TurnResponsePartData(
                turnId.Value,
                1,
                TurnResponsePartKind.Text.ToMachineText(),
                outcome.Summary,
                Payload: null));

        if (outcome.Payload is not null)
        {
            AddAfter(
                events,
                after,
                TurnEventSequence.ForPart(2),
                TurnEventKind.Part,
                new TurnResponsePartData(
                    turnId.Value,
                    2,
                    TurnResponsePartKind.Data.ToMachineText(),
                    Text: null,
                    JsonSerializer.Deserialize<JsonElement>(outcome.Payload)));
        }

        if (after < TurnEventSequence.Outcome)
        {
            var deliveries = await deliveryStore.FindByTurnIdAsync(turnId, cancellationToken);
            events.Add(Project(
                TurnEventSequence.Outcome,
                TurnEventKind.Outcome,
                new TurnStreamOutcomeData(
                    turnId.Value,
                    outcome.Status.ToString().ToLowerInvariant(),
                    outcome.Category.ToMachineText(),
                    outcome.Code,
                    outcome.Summary,
                    deliveries
                        .Select(delivery => new DeliveryView(
                            delivery.DeliveryId,
                            delivery.Channel,
                            delivery.Status.ToString().ToLowerInvariant(),
                            delivery.Attempts))
                        .ToList())));
        }

        return new TurnEventPage(events, ReachedTerminal: true);
    }

    private static void AddAfter<T>(
        ICollection<TurnStreamEvent> events,
        long after,
        long sequence,
        TurnEventKind kind,
        T data)
    {
        if (after < sequence)
        {
            events.Add(Project(sequence, kind, data));
        }
    }

    private static TurnStreamEvent Project<T>(long sequence, TurnEventKind kind, T data) =>
        new(sequence, kind.ToMachineText(), JsonSerializer.Serialize(data, JsonOptions));
}
