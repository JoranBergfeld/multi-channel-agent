using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Result of <see cref="IInboxStore.AcceptAsync"/>: the durable Turn that now owns the
/// <see cref="InboundTurn.NativeMessageId"/>, and whether it was already durably accepted before this
/// call. When two callers race to accept the same native message, <see cref="IInboxStore.AcceptAsync"/>
/// must express this atomically - the loser's <see cref="Turn"/> is the winner's Turn, not its own
/// locally constructed one - so callers never need to inspect a store-specific exception to find out.
/// </summary>
public sealed record InboxAcceptResult(InboundTurn Turn, bool WasAlreadyAccepted);

/// <summary>
/// Durable inbound acceptance boundary (the "inbox"). Implementations must make acceptance
/// idempotent by <see cref="InboundTurn.NativeMessageId"/> and make claimed work safe for a single
/// worker to process at a time. Inbox completion is recorded only through
/// <see cref="ITurnResultStore"/>, atomically with the Turn's Outcome and Deliveries, so this store
/// deliberately exposes no way to mark completion on its own that could bypass that invariant.
/// </summary>
public interface IInboxStore
{
    Task<InboundTurn?> FindByNativeMessageIdAsync(string nativeMessageId, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up the durably accepted Turn by its application-generated <see cref="TurnId"/> - used to
    /// authorize a read of its Outcome against the Turn's own <see cref="InboundTurn.ParticipantId"/>,
    /// never to bypass acceptance.
    /// </summary>
    Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically accepts <paramref name="turn"/> unless a Turn for the same
    /// <see cref="InboundTurn.NativeMessageId"/> is already durably accepted - including one accepted
    /// by a concurrent caller racing this same call - in which case that existing Turn is returned
    /// with <see cref="InboxAcceptResult.WasAlreadyAccepted"/> set, never a store-specific duplicate
    /// exception.
    /// </summary>
    Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, CancellationToken cancellationToken);

    Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken);
}
