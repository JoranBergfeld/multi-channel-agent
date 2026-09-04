using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="ImportCleanupCoordinator.SweepAsync"/>, so an expired Initial
/// Import stops occupying its Participant's one pending slot for that Inventory, its raw file is
/// discarded, and audit facts do not outlive their ninety days. An import's lifetime is ten minutes,
/// so five-minute granularity frees that slot promptly without anything having to poll for it.
/// </summary>
public sealed class ImportCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ImportCleanupWorker> logger) : BackgroundService
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
                var coordinator = scope.ServiceProvider.GetRequiredService<ImportCleanupCoordinator>();
                await coordinator.SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "An import cleanup pass failed.");
            }
        }
    }
}
