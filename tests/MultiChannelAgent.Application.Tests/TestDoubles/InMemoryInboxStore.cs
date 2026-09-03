using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>Minimal in-memory <see cref="IInboxStore"/> for Application-layer unit tests.</summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly List<InboundTurn> _turns = [];
    private readonly HashSet<Guid> _completed = [];

    public IReadOnlyList<InboundTurn> Turns => _turns;

    public Task<InboundTurn?> FindByNativeMessageIdAsync(string nativeMessageId, CancellationToken cancellationToken)
    {
        var match = _turns.FirstOrDefault(t => t.NativeMessageId == nativeMessageId);
        return Task.FromResult(match);
    }

    public Task AcceptAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        _turns.Add(turn);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken)
    {
        var pending = _turns
            .Where(t => !_completed.Contains(t.TurnId.Value))
            .Take(maxCount)
            .ToList();
        return Task.FromResult<IReadOnlyList<InboundTurn>>(pending);
    }

    /// <summary>
    /// Not part of <see cref="IInboxStore"/>: only <see cref="InMemoryTurnResultStore"/> calls this,
    /// mirroring how the SQL-backed store only marks inbox completion from within
    /// <c>SqlTurnResultStore</c>'s single atomic write.
    /// </summary>
    public Task MarkCompletedAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        _completed.Add(turnId.Value);
        return Task.CompletedTask;
    }
}
