using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Stores durable courtesy progress events independently of the terminal result transaction. Progress
/// expiry affects only these transient events and must leave the Turn's terminal <see cref="Outcome"/>
/// untouched.
/// </summary>
public interface ITurnProgressEventStore
{
    /// <summary>
    /// Atomically appends <paramref name="progressEvent"/> when its exact
    /// (<see cref="TurnProgressEvent.TurnId"/>, <see cref="TurnProgressEvent.Sequence"/>) pair does not
    /// already exist. Idempotency is keyed only by that exact pair: implementations must not assign a
    /// store-side counter, must not read first, and must not replace an existing event. Returns
    /// <see langword="true"/> for the first append of the pair and <see langword="false"/> for a
    /// concurrent or repeated duplicate of the same pair, rather than a store-specific duplicate
    /// exception.
    /// </summary>
    Task<bool> AppendAsync(TurnProgressEvent progressEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Reads all retained progress events for <paramref name="turnId"/> ordered by
    /// <see cref="TurnProgressEvent.Sequence"/> ascending.
    /// </summary>
    Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(TurnId turnId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes at most <paramref name="maxCount"/> progress events whose expiry is at or before
    /// <paramref name="now"/>. Implementations must delete only the exact TurnId/Sequence pairs
    /// selected as expired and must never delete or alter the Turn's terminal <see cref="Outcome"/>.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken);
}
