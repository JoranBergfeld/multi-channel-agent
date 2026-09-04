using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>What storing a proposal did to whatever was pending in that conversation before it.</summary>
public sealed record StoredProposalReplacement(bool SupersededExisting);

/// <summary>
/// The durable home of pending confirmation proposals.
///
/// One invariant dominates the contract and must be enforced by the database rather than by
/// convention: <b>at most one Pending proposal may exist per Participant and ChannelConversation</b>.
/// That is what makes "confirm" unambiguous - there is only ever one thing it could mean - and it is
/// why <see cref="StoreAsync"/> supersedes and inserts atomically rather than leaving a window in
/// which a conversation has two, or none.
///
/// Lookup is deliberately by Participant and ChannelConversation, never by token: a token belonging
/// to someone else, or to another conversation, cannot even be looked up here, so non-disclosure is
/// structural rather than a code path someone has to remember to write.
/// </summary>
public interface IConfirmationProposalStore
{
    /// <summary>The one Pending proposal for this Participant and ChannelConversation, or null when there is none.</summary>
    Task<ConfirmationProposal?> FindPendingAsync(
        ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores a new Pending proposal, atomically superseding whatever was Pending for the same
    /// Participant and ChannelConversation. A stale confirmation can therefore never execute the
    /// proposal a replacement replaced.
    /// </summary>
    Task<StoredProposalReplacement> StoreAsync(ConfirmationProposal proposal, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a Pending proposal to a terminal status. Returns false when it was not Pending any more,
    /// which is exactly how single use is enforced: the second confirmation, rejection, or
    /// invalidation of one proposal loses and must be answered as such rather than acting.
    /// </summary>
    Task<bool> SettleAsync(
        ProposalId proposalId, ProposalStatus status, DateTimeOffset settledAt, CancellationToken cancellationToken);

    /// <summary>The status of a proposal, or null when no such proposal is retained. For diagnosis and tests, never for authorization.</summary>
    Task<ProposalStatus?> FindStatusAsync(ProposalId proposalId, CancellationToken cancellationToken);

    /// <summary>
    /// Settles whatever is Pending for this Participant and ChannelConversation, returning how many
    /// rows moved (0 or 1). This is the one entry point for every invalidation that is not a
    /// confirmation or a rejection: access loss, an Inventory switch, and an interrupted Turn.
    /// </summary>
    Task<int> InvalidatePendingAsync(
        ParticipantId participantId,
        string channelConversationId,
        ProposalStatus status,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles up to <paramref name="maxRows"/> Pending proposals whose <c>ExpiresAt</c> is at or
    /// before <paramref name="now"/>. Reading also enforces expiry, so this is hygiene rather than
    /// the guarantee - it stops expired rows occupying the one-pending-per-conversation slot forever.
    /// </summary>
    Task<int> ExpirePendingBeforeAsync(DateTimeOffset now, int maxRows, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="maxRows"/> settled proposals settled at or before
    /// <paramref name="cutoff"/>. Settled rows are retained briefly - the proposal cleanup
    /// coordinator owns that window and hands this the settle instant it wants deleted - so a
    /// confirmation that arrives just after a rejection can still be answered truthfully rather than
    /// as "unknown proposal".
    /// </summary>
    Task<int> DeleteSettledBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken);
}
