using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The one place every invalidation that is <em>not</em> the Participant answering their proposal
/// lives. It runs once per Turn, immediately after the trusted context is assembled and before the
/// model is asked anything, so a proposal that must not survive this Turn is already settled by the
/// time any tool could reach it.
///
/// Rejection, replacement, expiry, and execution conflicts are handled where they happen -
/// <see cref="InventoryConfirmationService"/>, <see cref="IConfirmationProposalStore.StoreAsync"/>, and
/// the change-set store - because each of those already holds the context needed to decide.
/// </summary>
public sealed class ConfirmationProposalLifecycle(IConfirmationProposalStore proposalStore)
{
    /// <summary>
    /// Settles the pending proposal for this Turn's Participant and ChannelConversation when this
    /// Turn makes it untrustworthy, and returns the status it was settled with (or null when it was
    /// left alone). Returning the status rather than nothing lets a caller say what happened instead
    /// of leaving the Participant to discover their proposal quietly stopped working.
    /// </summary>
    public async Task<ProposalStatus?> ReconcileAsync(
        TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var conversationId = context.ChannelConversationId.Value;

        var pending = await proposalStore.FindPendingAsync(context.ParticipantId, conversationId, cancellationToken);
        if (pending is null)
        {
            return null;
        }

        // A cut-off utterance is not a statement of intent, and a conversation that has just been
        // interrupted is not one in which a stored approval should keep waiting to be triggered.
        var status = context switch
        {
            { WasInterrupted: true } => ProposalStatus.Interrupted,

            // Trusted context rechecks Membership every Turn. No Active Inventory now means access to
            // it was lost (or the selection was cleared), and a proposal bound to an Inventory the
            // Participant may no longer touch must never execute.
            { ActiveInventoryId: null } => ProposalStatus.AccessLost,

            // The conversation is working somewhere else now, so the proposal no longer describes what
            // the Participant is doing.
            { ActiveInventoryId: { } active } when active != pending.InventoryId => ProposalStatus.InventorySwitched,
            _ => (ProposalStatus?)null,
        };

        if (status is not { } terminal)
        {
            return null;
        }

        return await proposalStore.SettleAsync(pending.Id, terminal, now, cancellationToken) ? terminal : null;
    }
}
