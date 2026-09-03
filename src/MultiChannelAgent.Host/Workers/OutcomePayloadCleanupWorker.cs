using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="OutcomePayloadCleanupCoordinator.PurgeExpiredPayloadsAsync"/>, so
/// retained Outcome payloads - ephemeral projections of Inventory state - are discarded once they
/// expire instead of accumulating for the life of the database. It runs far less often than the Turn
/// and Delivery workers because expiry is measured in hours, not seconds.
/// </summary>
public sealed class OutcomePayloadCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OutcomePayloadCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<OutcomePayloadCleanupCoordinator>();
                await coordinator.PurgeExpiredPayloadsAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "An Outcome payload cleanup pass failed.");
            }
        }
    }
}
