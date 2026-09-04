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
    /// already exist. Returns <see langword="true"/> only for the first append; retries of the exact
    /// pair return <see langword="false"/> without replacing the existing event.
    /// </summary>
    Task<bool> AppendAsync(TurnProgressEvent progressEvent);

    /// <summary>
    /// Reads all retained progress events for <paramref name="turnId"/> ordered by
    /// <see cref="TurnProgressEvent.Sequence"/> ascending.
    /// </summary>
    Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(TurnId turnId);

    /// <summary>
    /// Deletes at most <paramref name="maxCount"/> progress events whose expiry is at or before
    /// <paramref name="now"/>. Implementations must delete only the exact TurnId/Sequence pairs
    /// selected as expired and must never delete or alter the Turn's terminal <see cref="Outcome"/>.
    /// </summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset now, int maxCount);
}
