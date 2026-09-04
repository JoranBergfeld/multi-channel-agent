using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

public sealed class InMemoryTurnProgressEventStore : ITurnProgressEventStore
{
    private readonly Lock gate = new();
    private readonly Dictionary<(Guid TurnId, long Sequence), TurnProgressEvent> events = [];
    private bool failNextAppend;

    public bool ModelWasCalled { get; set; }

    public bool WasAppendedBeforeFirstModelCall { get; private set; }

    public bool FailNextAppend
    {
        get
        {
            lock (gate)
            {
                return failNextAppend;
            }
        }
        set
        {
            lock (gate)
            {
                failNextAppend = value;
            }
        }
    }

    public Task<bool> AppendAsync(TurnProgressEvent progressEvent, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (failNextAppend)
            {
                failNextAppend = false;
                throw new InvalidOperationException("Synthetic progress append failure.");
            }

            var appended = events.TryAdd((progressEvent.TurnId.Value, progressEvent.Sequence), progressEvent);
            if (appended && events.Count == 1)
            {
                WasAppendedBeforeFirstModelCall = !ModelWasCalled;
            }

            return Task.FromResult(appended);
        }
    }

    public Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            IReadOnlyList<TurnProgressEvent> result = events.Values
                .Where(progressEvent => progressEvent.TurnId == turnId)
                .OrderBy(progressEvent => progressEvent.Sequence)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<int> DeleteExpiredAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var expiredKeys = events
                .Where(entry => entry.Value.ExpiresAt <= now)
                .OrderBy(entry => entry.Value.ExpiresAt)
                .ThenBy(entry => entry.Key.TurnId)
                .ThenBy(entry => entry.Key.Sequence)
                .Take(maxCount)
                .Select(entry => entry.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                events.Remove(key);
            }

            return Task.FromResult(expiredKeys.Count);
        }
    }
}
