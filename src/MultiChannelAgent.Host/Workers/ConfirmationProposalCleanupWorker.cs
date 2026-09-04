using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="ConfirmationProposalCleanupCoordinator.SweepAsync"/>, so an expired
/// confirmation proposal stops occupying its conversation's one pending slot and settled proposals do
/// not accumulate for the life of the database. A proposal's lifetime is ten minutes, so five-minute
/// granularity keeps that slot free promptly without anything having to poll for it.
/// </summary>
public sealed class ConfirmationProposalCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ConfirmationProposalCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<ConfirmationProposalCleanupCoordinator>();
                await coordinator.SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "A confirmation proposal cleanup pass failed.");
            }
        }
    }
}
