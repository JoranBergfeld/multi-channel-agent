using Microsoft.Extensions.Logging;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Claims durably accepted Turns and drives them to a terminal <see cref="Outcome"/> through the
/// scripted model boundary, atomically recording the Outcome, any requested Deliveries, and inbox
/// completion via <see cref="ITurnResultStore"/>. Runs under an exclusive lease so multiple hosted
/// replicas never process the same Turn twice, and exposes a deterministic one-shot operation so
/// tests can drive processing without timing a background loop. Enforces per-ChannelConversation
/// FIFO: a Turn that fails to reach a terminal Outcome this pass blocks every later Turn in its same
/// ChannelConversation for the remainder of this pass, while unrelated ChannelConversations proceed
/// independently.
/// </summary>
public sealed class TurnProcessingCoordinator(
    IInboxStore inboxStore,
    ITurnResultStore turnResultStore,
    ILeaseCoordinator leaseCoordinator,
    IModelBoundary modelBoundary,
    TurnExecutionContextFactory executionContextFactory,
    IToolDispatcher toolDispatcher,
    TimeProvider timeProvider,
    ILogger<TurnProcessingCoordinator> logger)
{
    private const string LeaseName = "turn-processing";
    private const int MaxBatchSize = 20;

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

        var pendingTurns = await inboxStore.ClaimPendingAsync(MaxBatchSize, cancellationToken);
        var processedCount = 0;

        // Per-conversation FIFO: pendingTurns is ordered FIFO (received order) globally, so within
        // any one ChannelConversation its Turns already appear in that same order here. Once a Turn
        // fails to reach a terminal Outcome this pass, every later Turn in that SAME
        // ChannelConversation must be left untouched (not even attempted) rather than let it complete
        // ahead of its still-pending predecessor - a later pass, once the predecessor is resolved,
        // safely retries the whole conversation from where it left off. Turns in a different
        // ChannelConversation are never blocked by this.
        var blockedConversations = new HashSet<ChannelConversationId>();

        foreach (var turn in pendingTurns)
        {
            if (blockedConversations.Contains(turn.ChannelConversationId))
            {
                continue;
            }

            try
            {
                await ProcessOneAsync(turn, cancellationToken);
                processedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-item isolation: one Turn failing to record its result (e.g. a transient SQL
                // fault) must not prevent later pending Turns in OTHER ChannelConversations from being
                // processed. ITurnResultStore.RecordAsync is atomic, so no partial Outcome/Delivery/
                // inbox state was written for this Turn - it remains Pending and a later pass safely
                // retries it from scratch.
                logger.LogError(ex, "Failed to process Turn {TurnId}; it remains pending for retry.", turn.TurnId);
                blockedConversations.Add(turn.ChannelConversationId);
            }
        }

        return processedCount;
    }

    private async Task ProcessOneAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var proposal = await modelBoundary.ProposeAsync(turn, cancellationToken);

        // The trusted TurnExecutionContext - Participant, ChannelConversation, Foundry conversation,
        // and current Active Inventory - is assembled here, from the durably-accepted Turn's own
        // identity plus a fresh authorization recheck, and is only ever built when a tool call was
        // actually proposed. It is never derived from anything the proposal itself claims.
        var decision = proposal.Kind == ModelProposalKind.Direct
            ? proposal.Direct!
            : await toolDispatcher.DispatchAsync(
                proposal.ToolCall!, await executionContextFactory.CreateAsync(turn, now, cancellationToken), now, cancellationToken);

        var outcome = decision.Status == OutcomeStatus.Completed
            ? Outcome.Completed(turn.TurnId, decision.Code, decision.Summary, now, decision.Payload)
            : Outcome.Failed(turn.TurnId, decision.Code, decision.Summary, now, decision.Payload);

        var deliveries = decision.Deliveries
            .Select(requested => Delivery.Request(turn.TurnId, requested.Channel, requested.Payload, now))
            .ToList();

        await turnResultStore.RecordAsync(outcome, deliveries, cancellationToken);
    }
}
