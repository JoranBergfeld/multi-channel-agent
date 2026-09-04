using Microsoft.Extensions.Logging;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Discards retained Turn progress markers once they have expired. Only the transient progress
/// stream is swept - the terminal Turn, its Outcome, and any Deliveries are left untouched - so
/// cleanup never alters the authoritative result of a Turn. Runs under its own exclusive lease and
/// exposes a deterministic one-shot operation so a host worker and tests can drive the sweep
/// without timing a loop.
/// </summary>
public sealed class TurnProgressEventCleanupCoordinator(
    ITurnProgressEventStore turnProgressEventStore,
    ILeaseCoordinator leaseCoordinator,
    TimeProvider timeProvider,
    ILogger<TurnProgressEventCleanupCoordinator> logger)
{
    private const string LeaseName = "turn-progress-cleanup";
    private const int MaxBatchSize = 500;

    public async Task<int> PurgeExpiredProgressAsync(CancellationToken cancellationToken)
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

        var deletedCount = await turnProgressEventStore.DeleteExpiredAsync(
            timeProvider.GetUtcNow(), MaxBatchSize, cancellationToken);

        if (deletedCount > 0)
        {
            logger.LogInformation("Deleted {DeletedCount} expired Turn progress events.", deletedCount);
        }

        return deletedCount;
    }
}
