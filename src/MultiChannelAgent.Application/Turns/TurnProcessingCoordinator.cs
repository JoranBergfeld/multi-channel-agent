using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Claims durably accepted Turns and drives them to a terminal <see cref="Outcome"/> through the
/// scripted model boundary, atomically recording the Outcome, any requested Deliveries, and inbox
/// completion via <see cref="ITurnResultStore"/>. Runs under an exclusive lease so multiple hosted
/// replicas never process the same Turn twice, and exposes a deterministic one-shot operation so
/// tests can drive processing without timing a background loop. Per-ChannelConversation FIFO is
/// owned by <see cref="IInboxStore.ClaimPendingAsync"/>, which only ever offers a conversation's
/// head; this coordinator additionally stops offering a conversation any further Turn for the rest of
/// the pass once its head fails, while unrelated ChannelConversations proceed independently.
///
/// It also reconciles pending confirmation state against the freshly assembled trusted context before
/// the Turn is interpreted, so an interrupted Turn, a switched Active Inventory, or lost access can
/// never leave a confirmable proposal behind.
/// </summary>
public sealed class TurnProcessingCoordinator(
    IInboxStore inboxStore,
    ITurnResultStore turnResultStore,
    ILeaseCoordinator leaseCoordinator,
    IModelBoundary modelBoundary,
    TurnExecutionContextFactory executionContextFactory,
    ConfirmationProposalLifecycle proposalLifecycle,
    IToolDispatcher toolDispatcher,
    TimeProvider timeProvider,
    ILogger<TurnProcessingCoordinator> logger)
{
    private const string LeaseName = "turn-processing";
    private const int MaxBatchSize = 20;

    /// <summary>
    /// How many conversation-head waves one pass may drain. The inbox only ever offers a
    /// ChannelConversation's head, so draining a backlog takes one wave per Turn in the deepest
    /// conversation; bounding the waves keeps a single pass's work (and the lease it holds) finite
    /// even under a large backlog, leaving the remainder for the next pass.
    /// </summary>
    private const int MaxWavesPerPass = 20;

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        await using var lease = await leaseCoordinator.TryAcquireAsync(
            LeaseName,
            ownerId: Guid.NewGuid().ToString("N"),
            duration: TimeSpan.FromSeconds(30),
            cancellationToken);

        if (lease is null)
        {
            return 0;
        }

        var processedCount = 0;

        // Per-conversation FIFO is enforced by the inbox itself: each claim offers only a
        // ChannelConversation's head, so a later Turn can never be claimed - let alone processed -
        // while an earlier one in the same conversation is still outstanding. This pass therefore
        // drains a backlog by re-claiming heads, and stops offering a conversation any further Turn
        // once its current head fails to reach a terminal Outcome: that head stays pending, so a
        // later pass (once the fault clears) resumes the conversation exactly where it left off.
        // Turns in other ChannelConversations are never blocked by this.
        var blockedConversations = new HashSet<ChannelConversationId>();

        for (var wave = 0; wave < MaxWavesPerPass; wave++)
        {
            var claimedHeads = await inboxStore.ClaimPendingAsync(MaxBatchSize, cancellationToken);
            var progressed = false;

            foreach (var turn in claimedHeads)
            {
                if (blockedConversations.Contains(turn.ChannelConversationId))
                {
                    continue;
                }

                try
                {
                    await ProcessOneAsync(turn, cancellationToken);
                    processedCount++;
                    progressed = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Per-item isolation: one Turn failing to record its result (e.g. a transient SQL
                    // fault) must not prevent pending Turns in OTHER ChannelConversations from being
                    // processed. ITurnResultStore.RecordAsync is atomic, so no partial Outcome/
                    // Delivery/inbox state was written for this Turn - it remains Pending and a later
                    // pass safely retries it from scratch.
                    logger.LogError(ex, "Failed to process Turn {TurnId}; it remains pending for retry.", turn.TurnId);
                    blockedConversations.Add(turn.ChannelConversationId);
                }
            }

            if (!progressed)
            {
                break;
            }
        }

        return processedCount;
    }

    private async Task ProcessOneAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // The trusted TurnExecutionContext - Participant, ChannelConversation, Foundry conversation,
        // and current Active Inventory - is assembled from the durably-accepted Turn's own identity
        // plus a fresh authorization recheck, before the model is asked anything and for EVERY Turn,
        // not only ones that end up calling a tool: a Turn answered directly belongs to the same
        // conversation, and the model boundary is given that conversation to continue. It is never
        // derived from anything the proposal itself claims.
        var executionContext = await executionContextFactory.CreateAsync(turn, now, cancellationToken);

        // Settled before the model is asked anything, so an interrupted Turn, a switched Active
        // Inventory, or lost access can never leave a confirmable proposal behind for this Turn - or
        // any later one - to trigger.
        await proposalLifecycle.ReconcileAsync(executionContext, now, cancellationToken);

        var proposal = await modelBoundary.ProposeAsync(
            turn,
            new ModelInvocationContext(executionContext.FoundryConversationId, executionContext.FoundryConversationGeneration, turn.Locale),
            cancellationToken);

        var decision = proposal.Kind == ModelProposalKind.Direct
            ? proposal.Direct!
            : await toolDispatcher.DispatchAsync(proposal.ToolCall!, executionContext, now, cancellationToken);

        var outcome = Outcome.Record(
            turn.TurnId, decision.Category, decision.Code, decision.Summary, now, decision.Payload, decision.PayloadRetention);

        var deliveries = decision.Deliveries
            .Select(requested => Delivery.Request(turn.TurnId, requested.Channel, requested.Payload, now))
            .ToList();

        await turnResultStore.RecordAsync(outcome, deliveries, cancellationToken);
    }
}
