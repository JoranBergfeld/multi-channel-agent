using Microsoft.Extensions.Logging;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// Discards recorded Outcome payloads once they have expired. The payload is an ephemeral projection
/// of Inventory state, retained only so a Participant can pick an answer back up after a disconnect;
/// without scheduled cleanup it would accumulate forever and keep serving an increasingly stale copy
/// of state the database is authoritative for. Only the projection is dropped - the Outcome's
/// category, code, and summary are permanent, so a Turn never stops having an answer.
///
/// Runs under its own exclusive lease, so several hosted replicas never duplicate the work, and
/// exposes a deterministic one-shot operation so tests can drive it without timing a background loop.
/// </summary>
public sealed class OutcomePayloadCleanupCoordinator(
    IOutcomeStore outcomeStore,
    ILeaseCoordinator leaseCoordinator,
    TimeProvider timeProvider,
    ILogger<OutcomePayloadCleanupCoordinator> logger)
{
    private const string LeaseName = "outcome-payload-cleanup";

    /// <summary>Bounds one pass so a large backlog is drained over several passes instead of one long transaction.</summary>
    private const int MaxBatchSize = 500;

    public async Task<int> PurgeExpiredPayloadsAsync(CancellationToken cancellationToken)
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

        var purgedCount = await outcomeStore.DiscardExpiredPayloadsAsync(timeProvider.GetUtcNow(), MaxBatchSize, cancellationToken);

        if (purgedCount > 0)
        {
            logger.LogInformation("Discarded {PurgedCount} expired Outcome payloads.", purgedCount);
        }

        return purgedCount;
    }
}
