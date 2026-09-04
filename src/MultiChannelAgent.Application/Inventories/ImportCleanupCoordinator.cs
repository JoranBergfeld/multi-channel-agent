using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Expires pending Initial Imports whose ten minutes have run out - discarding their raw files with
/// them - discards settled ones past retention, and sweeps audit facts past their ninety days.
///
/// The first two mirror <see cref="ConfirmationProposalCleanupCoordinator"/> exactly. The third is
/// new to the whole system: <see cref="AuditFact.RetentionDays"/> has said ninety since audits
/// existed, and nothing enforced it. #34 requires that only the specified ninety-day semantic facts
/// remain, so the sweep lives here and covers every audit fact rather than only the import one -
/// there is no honest way to retain one kind for ninety days and another forever.
///
/// Runs under its own exclusive lease, so several hosted replicas never duplicate the work, and
/// exposes a deterministic one-shot operation so tests can drive it without timing a background loop.
/// </summary>
public sealed class ImportCleanupCoordinator(
    IImportProposalStore proposalStore,
    IInventoryAuditRetentionStore auditStore,
    ILeaseCoordinator leaseCoordinator,
    TimeProvider timeProvider,
    ILogger<ImportCleanupCoordinator> logger)
{
    private const string LeaseName = "import-cleanup";

    /// <summary>Bounds one pass so a large backlog is drained over several passes instead of one long transaction.</summary>
    private const int MaxBatchSize = 500;

    /// <summary>How long a settled import is retained, so a late answer can still be told what happened.</summary>
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
        var audits = await auditStore.DeleteOccurredBeforeAsync(
            now.AddDays(-AuditFact.RetentionDays), MaxBatchSize, cancellationToken);

        if (expired > 0 || deleted > 0 || audits > 0)
        {
            logger.LogInformation(
                "Expired {ExpiredCount} pending imports, discarded {DeletedCount} settled ones, and swept {AuditCount} audit facts past retention.",
                expired,
                deleted,
                audits);
        }

        return expired + deleted + audits;
    }
}
