using Microsoft.Extensions.Logging;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Claims durably accepted Turns and drives them to a terminal <see cref="Outcome"/> through the
/// scripted model boundary, atomically recording the Outcome, any requested Deliveries, and inbox
/// completion via <see cref="ITurnResultStore"/>. Runs under an exclusive lease so multiple hosted
/// replicas never process the same Turn twice, and exposes a deterministic one-shot operation so
/// tests can drive processing without timing a background loop.
/// </summary>
public sealed class TurnProcessingCoordinator(
    IInboxStore inboxStore,
    ITurnResultStore turnResultStore,
    ILeaseCoordinator leaseCoordinator,
    IModelBoundary modelBoundary,
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

        foreach (var turn in pendingTurns)
        {
            try
            {
                await ProcessOneAsync(turn, cancellationToken);
                processedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-item isolation: one Turn failing to record its result (e.g. a transient SQL
                // fault) must not prevent later pending Turns in this batch from being processed.
                // ITurnResultStore.RecordAsync is atomic, so no partial Outcome/Delivery/inbox state
                // was written for this Turn - it remains Pending and a later pass safely retries it
                // from scratch.
                logger.LogError(ex, "Failed to process Turn {TurnId}; it remains pending for retry.", turn.TurnId);
            }
        }

        return processedCount;
    }

    private async Task ProcessOneAsync(InboundTurn turn, CancellationToken cancellationToken)
    {
        var decision = await modelBoundary.DecideAsync(turn, cancellationToken);
        var now = timeProvider.GetUtcNow();

        var outcome = decision.Status == OutcomeStatus.Completed
            ? Outcome.Completed(turn.TurnId, decision.Code, decision.Summary, now)
            : Outcome.Failed(turn.TurnId, decision.Code, decision.Summary, now);

        var deliveries = decision.Deliveries
            .Select(requested => Delivery.Request(turn.TurnId, requested.Channel, requested.Payload, now))
            .ToList();

        await turnResultStore.RecordAsync(outcome, deliveries, cancellationToken);
    }
}
