using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Expires pending confirmation proposals whose ten minutes have run out, then discards settled ones
/// past retention.
///
/// Reading a proposal already enforces expiry, so this is not what makes an old confirmation safe -
/// it is what stops an expired proposal occupying the one-pending-per-conversation slot forever, and
/// what stops settled rows accumulating for the life of the database. Settled rows are kept briefly
/// on purpose: a confirmation that arrives moments after a rejection can then be answered truthfully
/// instead of as "unknown proposal".
///
/// Runs under its own exclusive lease, so several hosted replicas never duplicate the work, and
/// exposes a deterministic one-shot operation so tests can drive it without timing a background loop.
/// </summary>
public sealed class ConfirmationProposalCleanupCoordinator(
    IConfirmationProposalStore proposalStore,
    ILeaseCoordinator leaseCoordinator,
    TimeProvider timeProvider,
    ILogger<ConfirmationProposalCleanupCoordinator> logger)
{
    private const string LeaseName = "confirmation-proposal-cleanup";

    /// <summary>Bounds one pass so a large backlog is drained over several passes instead of one long transaction.</summary>
    private const int MaxBatchSize = 500;

    /// <summary>How long a settled proposal is retained, so a late answer can still be told what happened.</summary>
    public static readonly TimeSpan SettledRetention = TimeSpan.FromHours(24);

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
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

        var now = timeProvider.GetUtcNow();
        var expired = await proposalStore.ExpirePendingBeforeAsync(now, MaxBatchSize, cancellationToken);
        var deleted = await proposalStore.DeleteSettledBeforeAsync(now - SettledRetention, MaxBatchSize, cancellationToken);

        if (expired > 0 || deleted > 0)
        {
            logger.LogInformation(
                "Expired {ExpiredCount} pending confirmation proposals and discarded {DeletedCount} settled ones.", expired, deleted);
        }

        return expired + deleted;
    }
}
