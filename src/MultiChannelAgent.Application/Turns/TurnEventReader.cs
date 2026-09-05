using System.Text.Json;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>One event on a Turn's stream: its issued identity, its name, and its already-serialized single-line JSON body.</summary>
public sealed record TurnStreamEvent(long Sequence, string Name, string Data);

/// <summary>
/// Everything a Turn's stream has to say beyond a given resume point, and whether the stream is
/// finished. <see cref="ReachedTerminal"/> is true exactly when the Turn has a recorded Outcome -
/// including when the caller already had it - so a channel adapter knows to stop rather than to
/// guess from an empty page.
/// </summary>
public sealed record TurnEventPage(IReadOnlyList<TurnStreamEvent> Events, bool ReachedTerminal);

/// <summary>Wire body of the <c>accepted</c> event.</summary>
public sealed record TurnAcceptedData(Guid TurnId, DateTimeOffset ReceivedAt);

/// <summary>Wire body of the <c>processing</c> event.</summary>
public sealed record TurnProcessingData(Guid TurnId, DateTimeOffset StartedAt);

/// <summary>
/// Wire body of one <c>part</c> event: one channel-neutral piece of the answer. Exactly one of
/// <see cref="Text"/> and <see cref="Payload"/> is ever present, and neither is ever a raw model
/// token - a text part is the recorded human summary and a data part is the recorded typed
/// projection.
/// </summary>
public sealed record TurnResponsePartData(
    Guid TurnId,
    int Order,
    string Kind,
    string? Text,
    JsonElement? Payload);

/// <summary>
/// Wire body of the one terminal <c>outcome</c> event. It deliberately carries no payload: the typed
/// projection already arrived as a data part, so the stream never sends it twice - which also means a
/// short-lived confirmation token is never duplicated on the wire.
/// </summary>
public sealed record TurnStreamOutcomeData(
    Guid TurnId,
    string Status,
    string Category,
    string Code,
    string Summary,
    IReadOnlyList<DeliveryView> Deliveries);

/// <summary>
/// The single authority on what one Turn's resumable event stream is. Channel adapters serialize what
/// this returns and nothing more, so every rule the stream depends on lives here exactly once.
///
/// Only one of the four event kinds is read from a durable event row. The others are projected from
/// state this system already keeps permanently - acceptance from the Turn's own inbox record, the
/// answer's parts and its terminal Outcome from the recorded <see cref="Outcome"/> and its
/// Deliveries - which is what makes the stream survive a process restart, replay identically however
/// often it is resumed, and hold no second copy of a payload whose retention is already governed
/// elsewhere. When that payload has expired, the answer simply streams without its data part, exactly
/// as <see cref="TurnOutcomeReader"/> already serves it without one.
///
/// A caller may only ever read their own Turn: <see cref="ReadAfterAsync"/> returns null - the same
/// shape as "no such Turn" - for a Turn that exists but belongs to a different Participant, so a
/// caller can never learn that some other Participant's Turn exists.
/// </summary>
public sealed class TurnEventReader(
    IInboxStore inboxStore,
    ITurnProgressEventStore progressEventStore,
    IOutcomeStore outcomeStore,
    IDeliveryStore deliveryStore)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Projects the caller's Turn stream strictly after <paramref name="afterSequence"/> from its
    /// durable acceptance, retained processing marker, recorded semantic response parts, terminal
    /// Outcome, and Deliveries.
    /// </summary>
    /// <param name="turnId">The Turn whose resumable event stream is requested.</param>
    /// <param name="requestingParticipantId">The Participant requesting the stream.</param>
    /// <param name="afterSequence">The last sequence already observed; only later events are returned.</param>
    /// <param name="cancellationToken">Cancels the durable reads used to project the page.</param>
    /// <returns>
    /// Null for both an unknown Turn and a Turn owned by another Participant, deliberately making the
    /// two cases indistinguishable. Otherwise, the events after the resume point and whether an
    /// Outcome has been recorded; a terminal page remains terminal even when it is empty because the
    /// caller has already observed every event.
    /// </returns>
    public async Task<TurnEventPage?> ReadAfterAsync(
        TurnId turnId,
        ParticipantId requestingParticipantId,
        long afterSequence,
        CancellationToken cancellationToken)
    {
        var turn = await inboxStore.FindByTurnIdAsync(turnId, cancellationToken);
        if (turn is null || turn.ParticipantId != requestingParticipantId)
        {
            return null;
        }

        var events = new List<TurnStreamEvent>();
        if (afterSequence < TurnEventSequence.Accepted)
        {
            events.Add(Project(
                TurnEventSequence.Accepted,
                TurnEventKind.Accepted,
                new TurnAcceptedData(turnId.Value, turn.ReceivedAt)));
        }

        if (afterSequence < TurnEventSequence.Processing)
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
            afterSequence,
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
                afterSequence,
                TurnEventSequence.ForPart(2),
                TurnEventKind.Part,
                new TurnResponsePartData(
                    turnId.Value,
                    2,
                    TurnResponsePartKind.Data.ToMachineText(),
                    Text: null,
                    JsonSerializer.Deserialize<JsonElement>(outcome.Payload)));
        }

        if (afterSequence < TurnEventSequence.Outcome)
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
        long afterSequence,
        long sequence,
        TurnEventKind kind,
        T data)
    {
        if (afterSequence < sequence)
        {
            events.Add(Project(sequence, kind, data));
        }
    }

    private static TurnStreamEvent Project<T>(long sequence, TurnEventKind kind, T data) =>
        new(sequence, kind.ToMachineText(), JsonSerializer.Serialize(data, JsonOptions));
}
