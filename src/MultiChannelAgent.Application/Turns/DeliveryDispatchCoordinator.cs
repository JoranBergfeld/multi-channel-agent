namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Claims pending outbox <see cref="MultiChannelAgent.Domain.Turns.Delivery"/> records and dispatches
/// them via <see cref="IDeliverySender"/>, independently of Turn processing. A failed attempt only
/// increments the attempt count and leaves the Delivery pending for the next dispatch pass to retry.
/// </summary>
public sealed class DeliveryDispatchCoordinator(
    IDeliveryStore deliveryStore,
    ILeaseCoordinator leaseCoordinator,
    IDeliverySender deliverySender,
    TimeProvider timeProvider)
{
    private const string LeaseName = "delivery-dispatch";
    private const int MaxBatchSize = 20;

    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
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

        var pendingDeliveries = await deliveryStore.ClaimPendingAsync(MaxBatchSize, cancellationToken);
        var dispatchedCount = 0;

        foreach (var delivery in pendingDeliveries)
        {
            var succeeded = await deliverySender.TrySendAsync(delivery, cancellationToken);

            var updated = succeeded
                ? delivery.MarkDelivered(timeProvider.GetUtcNow())
                : delivery.MarkAttemptFailed();

            await deliveryStore.SaveAsync(updated, cancellationToken);

            if (succeeded)
            {
                dispatchedCount++;
            }
        }

        return dispatchedCount;
    }
}
