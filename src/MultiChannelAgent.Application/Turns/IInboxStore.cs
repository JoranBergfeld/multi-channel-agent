using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Durable inbound acceptance boundary (the "inbox"). Implementations must make acceptance
/// idempotent by <see cref="InboundTurn.NativeMessageId"/> and make claimed work safe for a single
/// worker to process at a time.
/// </summary>
public interface IInboxStore
{
    Task<InboundTurn?> FindByNativeMessageIdAsync(string nativeMessageId, CancellationToken cancellationToken);

    Task AcceptAsync(InboundTurn turn, CancellationToken cancellationToken);

    Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken);

    Task MarkCompletedAsync(TurnId turnId, CancellationToken cancellationToken);
}
