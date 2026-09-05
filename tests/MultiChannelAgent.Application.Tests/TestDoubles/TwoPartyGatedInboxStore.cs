using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// Deterministically reproduces the exact concurrent-delivery race the durable-acceptance contract
/// must survive: two callers that both call <see cref="FindByNativeMessageIdAsync"/> and both observe
/// absence before either has accepted anything, then both race into <see cref="AcceptAsync"/> at the
/// same instant. Rather than relying on real thread-scheduling luck to line the two calls up, this
/// decorator holds each caller at the gate between "checked, found nothing" and "now inserting" until
/// its counterpart has reached the same gate, so the race is forced every run instead of only
/// sometimes.
/// </summary>
public sealed class TwoPartyGatedInboxStore(IInboxStore inner, TaskCompletionSource ownReady, Task otherReady) : IInboxStore
{
    public Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken) =>
        inner.FindByNativeMessageIdAsync(key, cancellationToken);

    public Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken) =>
        inner.FindByTurnIdAsync(turnId, cancellationToken);

    public async Task<InboxAcceptResult> AcceptAsync(
        InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken)
    {
        ownReady.TrySetResult();
        await otherReady;

        return await inner.AcceptAsync(turn, binding, cancellationToken);
    }

    /// <summary>
    /// The same convenience overload <see cref="InMemoryInboxStore"/> keeps, for tests that race
    /// acceptance without caring which generation each racer resolved.
    /// </summary>
    public Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, CancellationToken cancellationToken) =>
        AcceptAsync(
            turn,
            FoundryConversationBinding.CreateFirstGeneration(
                turn.ParticipantId, turn.ChannelConversationId, turn.ReceivedAt),
            cancellationToken);

    public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken) =>
        inner.FindCapturedBindingAsync(turnId, cancellationToken);

    public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken) =>
        inner.ClaimPendingAsync(maxCount, cancellationToken);
}
