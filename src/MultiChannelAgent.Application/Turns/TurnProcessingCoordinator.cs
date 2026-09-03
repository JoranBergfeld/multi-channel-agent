using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Claims durably accepted Turns and drives them to a terminal <see cref="Outcome"/> through the
/// scripted model boundary, writing any requested Deliveries to the outbox. Runs under an exclusive
/// lease so multiple hosted replicas never process the same Turn twice, and exposes a deterministic
/// one-shot operation so tests can drive processing without timing a background loop.
/// </summary>
public sealed class TurnProcessingCoordinator(
    IInboxStore inboxStore,
    IOutcomeStore outcomeStore,
    IDeliveryStore deliveryStore,
    ILeaseCoordinator leaseCoordinator,
    IModelBoundary modelBoundary,
    TimeProvider timeProvider)
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
            await ProcessOneAsync(turn, cancellationToken);
            processedCount++;
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

        await outcomeStore.SaveAsync(outcome, cancellationToken);

        foreach (var requested in decision.Deliveries)
        {
            var delivery = Delivery.Request(turn.TurnId, requested.Channel, requested.Payload, now);
            await deliveryStore.SaveAsync(delivery, cancellationToken);
        }

        await inboxStore.MarkCompletedAsync(turn.TurnId, cancellationToken);
    }
}
