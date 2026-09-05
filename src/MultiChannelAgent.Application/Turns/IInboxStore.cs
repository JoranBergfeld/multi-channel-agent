using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Result of <see cref="IInboxStore.AcceptAsync"/>: the durable Turn that now owns the
/// <see cref="InboundTurn.NativeMessageKey"/>, and whether it was already durably accepted before this
/// call. When two callers race to accept the same native message, <see cref="IInboxStore.AcceptAsync"/>
/// must express this atomically - the loser's <see cref="Turn"/> is the winner's Turn, not its own
/// locally constructed one - so callers never need to inspect a store-specific exception to find out.
/// </summary>
public sealed record InboxAcceptResult(InboundTurn Turn, bool WasAlreadyAccepted);

/// <summary>
/// The Foundry conversation identity a Turn was accepted under, read back for processing. Captured at
/// acceptance rather than resolved when the Turn is finally claimed, so a conversation reset between
/// those two moments can never move already-accepted work into the history the reset created.
/// </summary>
public sealed record CapturedConversationBinding(FoundryConversationId FoundryConversationId, int Generation);

/// <summary>
/// Durable inbound acceptance boundary (the "inbox"). Implementations must make acceptance
/// idempotent by <see cref="InboundTurn.NativeMessageKey"/> - the native id together with the
/// Participant and ChannelConversation scope that issued it, never the bare id - and make claimed
/// work safe for a single worker to process at a time. Inbox completion is recorded only through
/// <see cref="ITurnResultStore"/>, atomically with the Turn's Outcome and Deliveries, so this store
/// deliberately exposes no way to mark completion on its own that could bypass that invariant.
/// </summary>
public interface IInboxStore
{
    Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up the durably accepted Turn by its application-generated <see cref="TurnId"/> - used to
    /// authorize a read of its Outcome against the Turn's own <see cref="InboundTurn.ParticipantId"/>,
    /// never to bypass acceptance.
    /// </summary>
    Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically accepts <paramref name="turn"/> - stamped with the Foundry conversation generation
    /// <paramref name="binding"/> it was accepted under - unless a Turn for the same
    /// <see cref="InboundTurn.NativeMessageKey"/> is already durably accepted, including one accepted
    /// by a concurrent caller racing this same call, in which case that existing Turn is returned
    /// with <see cref="InboxAcceptResult.WasAlreadyAccepted"/> set, never a store-specific duplicate
    /// exception. The loser's binding is never written over the winner's: the conversation the
    /// winning Turn was accepted under is the one it keeps.
    /// </summary>
    Task<InboxAcceptResult> AcceptAsync(
        InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken);

    /// <summary>
    /// The Foundry conversation this Turn was accepted under, or null for a Turn accepted before that
    /// was captured. Never used for authorization - only to continue the right conversation.
    /// </summary>
    Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken);

    /// <summary>
    /// Offers pending work in a shape that makes per-ChannelConversation FIFO impossible to break:
    /// each ChannelConversation contributes at most its head - the earliest-accepted Turn that has
    /// not yet completed - so a later Turn is never claimable while an earlier one in the same
    /// conversation is still outstanding, whatever the batch limit, pass count, or lease boundary.
    /// Acceptance order is durable and monotonic per conversation, so Turns accepted in the same
    /// instant still order deterministically. Different ChannelConversations never block each other.
    /// </summary>
    Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken);
}
