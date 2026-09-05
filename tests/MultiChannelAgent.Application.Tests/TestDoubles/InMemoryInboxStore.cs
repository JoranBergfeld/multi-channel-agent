using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>Minimal in-memory <see cref="IInboxStore"/> for Application-layer unit tests.</summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly object _gate = new();
    private readonly List<InboundTurn> _turns = [];
    private readonly HashSet<Guid> _completed = [];
    private readonly Dictionary<Guid, CapturedConversationBinding> _capturedBindings = [];

    public IReadOnlyList<InboundTurn> Turns => _turns;

    public Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken)
    {
        InboundTurn? match;
        lock (_gate)
        {
            match = _turns.FirstOrDefault(t => t.NativeMessageKey == key);
        }

        return Task.FromResult(match);
    }

    public Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        InboundTurn? match;
        lock (_gate)
        {
            match = _turns.FirstOrDefault(t => t.TurnId == turnId);
        }

        return Task.FromResult(match);
    }

    /// <summary>
    /// Mirrors the atomicity <see cref="IInboxStore.AcceptAsync"/> requires from a real store: a lock
    /// makes the "is one already accepted for this native message key" check and the insert a single
    /// indivisible step, so concurrent callers racing this method converge on whichever Turn actually
    /// wins, exactly like the real unique index at the database does. The loser's binding is
    /// discarded with its Turn: the winner keeps the conversation it was accepted under.
    /// </summary>
    public Task<InboxAcceptResult> AcceptAsync(
        InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var existing = _turns.FirstOrDefault(t => t.NativeMessageKey == turn.NativeMessageKey);
            if (existing is not null)
            {
                return Task.FromResult(new InboxAcceptResult(existing, WasAlreadyAccepted: true));
            }

            _turns.Add(turn);
            _capturedBindings[turn.TurnId.Value] =
                new CapturedConversationBinding(binding.FoundryConversationId, binding.Generation);

            return Task.FromResult(new InboxAcceptResult(turn, WasAlreadyAccepted: false));
        }
    }

    /// <summary>
    /// Accepts a Turn into a first-generation binding for its own conversation. Not part of
    /// <see cref="IInboxStore"/> - it exists so the many tests that predate captured bindings, and
    /// genuinely do not care which generation a Turn was accepted under, keep saying what they mean
    /// instead of restating an irrelevant detail. Tests that DO care pass the binding explicitly.
    /// </summary>
    public Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, CancellationToken cancellationToken) =>
        AcceptAsync(
            turn,
            FoundryConversationBinding.CreateFirstGeneration(
                turn.ParticipantId, turn.ChannelConversationId, turn.ReceivedAt),
            cancellationToken);

    public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _capturedBindings.TryGetValue(turnId.Value, out var captured);
            return Task.FromResult(captured);
        }
    }

    /// <summary>
    /// Mirrors the SQL store's conversation-head selection: each ChannelConversation offers at most
    /// its earliest still-outstanding Turn, in acceptance order, so a later Turn is never claimable
    /// while an earlier one in the same conversation is unfinished.
    /// </summary>
    public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken)
    {
        List<InboundTurn> pending;
        lock (_gate)
        {
            pending = _turns
                .Where(t => !_completed.Contains(t.TurnId.Value))
                .GroupBy(t => t.ChannelConversationId)
                .Select(conversation => conversation.First())
                .Take(maxCount)
                .ToList();
        }

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
