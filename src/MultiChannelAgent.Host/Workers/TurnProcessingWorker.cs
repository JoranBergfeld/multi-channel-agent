using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="TurnProcessingCoordinator.ProcessPendingAsync"/> in its own DI
/// scope. The coordinator's own lease makes concurrent passes (from this loop or from tests calling
/// the coordinator directly) safe: at most one pass actually claims and processes work at a time.
/// </summary>
public sealed class TurnProcessingWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<TurnProcessingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
                await coordinator.ProcessPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "A Turn processing pass failed.");
            }
        }
    }
}
