using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// What the post-dispatch pass found: whether the conversation this Turn belongs to had been left
/// behind by the time its work was done, and the status of anything it settled there. They are
/// separate answers on purpose. A reset that committed before this Turn's proposal was stored settles
/// it in its own transaction, so this pass finds nothing left to settle - and the Turn must still not
/// answer with a confirmation, because the proposal it would offer has already stopped being one.
/// </summary>
public sealed record SupersededConversationSettlement(bool ConversationWasSuperseded, ProposalStatus? Settled)
{
    public static readonly SupersededConversationSettlement StillCurrent = new(false, null);
}

/// <summary>
/// The one place every invalidation that is <em>not</em> the Participant answering their proposal
/// lives. <see cref="ReconcileAsync"/> runs once per Turn, immediately after the trusted context is
/// assembled and before the model is asked anything, so a proposal that must not survive this Turn is
/// already settled by the time any tool could reach it.
///
/// <see cref="SettleSupersededConversationAsync"/> runs once more after dispatch, because the one
/// thing that first pass cannot cover is a proposal this very Turn had not created yet: a reset
/// landing mid-processing finds nothing to settle, and the Turn then goes on to store a proposal into
/// a conversation the Participant has already left.
///
/// Rejection, replacement, expiry, and execution conflicts are handled where they happen -
/// <see cref="InventoryConfirmationService"/>, <see cref="IConfirmationProposalStore.StoreAsync"/>, and
/// the change-set store - because each of those already holds the context needed to decide.
/// </summary>
public sealed class ConfirmationProposalLifecycle(
    IConfirmationProposalStore proposalStore, IFoundryConversationBindingStore bindingStore)
{
    /// <summary>The machine code an answer carries when a reset is why nothing is confirmable any more.</summary>
    public const string ConversationResetCode = "conversation_reset";

    private const string ConversationResetSummary =
        "That was proposed in a conversation you have since left, so there is nothing to confirm here.";

    /// <summary>
    /// What a Turn is answered with when the work it proposed stopped being confirmable before it
    /// could ever be offered. It deliberately carries no proposal payload and therefore no
    /// confirmation token: handing back a token that can never be redeemed would be worse than saying
    /// nothing. The category is a conflict with current state, not a failure - the Turn was processed
    /// exactly as asked, and the conversation it was asked in simply moved on.
    /// </summary>
    public static readonly ModelDecision ConversationResetAnswer = new()
    {
        Category = OutcomeCategory.Conflict,
        Code = ConversationResetCode,
        Summary = ConversationResetSummary,
        Deliveries = [new RequestedDelivery(StockToolDispatcher.ResponseChannel, ConversationResetSummary)],
    };

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
            // The strongest statement of the four: this Turn belongs to a conversation the Participant
            // has already left, so nothing waiting in it is still an open question.
            { AcceptedInSupersededConversation: true } => ProposalStatus.ConversationReset,

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

    /// <summary>
    /// Run after this Turn's work is done and before its Outcome is recorded: re-reads the binding and
    /// settles whatever is pending in this conversation when the generation the Turn was accepted
    /// under has been left behind. It deliberately ignores
    /// <see cref="TurnExecutionContext.AcceptedInSupersededConversation"/> - that flag answered the
    /// question at context assembly, and the whole point of this second pass is the window that opens
    /// after it.
    ///
    /// Why this closes that window. Write P for the instant this Turn's proposal became durable, S
    /// for the binding read below, and U and R for the rotation's own settle statement and its
    /// commit, with U before R. P always precedes S, because the proposal is stored during dispatch
    /// and this runs after dispatch. If P precedes U, the rotation's settle sees a durable Pending
    /// proposal and settles it inside its own transaction
    /// (<see cref="IConversationRotationStore"/>) - which is exactly why the answer below reports the
    /// conversation as superseded even when it settled nothing itself. Otherwise U precedes P, so
    /// there was nothing for the rotation to settle and this pass must catch it: it does whenever S
    /// observes the rotated binding, which R preceding S guarantees, and which a read that serializes
    /// against the in-flight rotation gives for the remaining U-P-S-R interleaving.
    /// </summary>
    public async Task<SupersededConversationSettlement> SettleSupersededConversationAsync(
        TurnExecutionContext context, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A read that fails is not evidence the conversation is still current, so it is left to
        // propagate: the Turn records no Outcome and is retried, rather than completing on a guess.
        var current = await bindingStore.GetOrCreateAsync(
            context.ParticipantId, context.ChannelConversationId, now, cancellationToken);

        if (context.FoundryConversationGeneration >= current.Generation)
        {
            return SupersededConversationSettlement.StillCurrent;
        }

        // One set-based settle rather than a read followed by a write: nothing here needs to know
        // which proposal it is, and anything Pending in a conversation that has been left behind is
        // by definition no longer confirmable.
        var settled = await proposalStore.InvalidatePendingAsync(
            context.ParticipantId,
            context.ChannelConversationId.Value,
            ProposalStatus.ConversationReset,
            now,
            cancellationToken);

        return new SupersededConversationSettlement(
            ConversationWasSuperseded: true, settled > 0 ? ProposalStatus.ConversationReset : null);
    }
}
