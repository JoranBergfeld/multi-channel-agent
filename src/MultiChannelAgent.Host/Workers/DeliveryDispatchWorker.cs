using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="DeliveryDispatchCoordinator.DispatchPendingAsync"/> independently
/// of Turn processing, so Delivery retries never rerun model planning or mutation.
/// </summary>
public sealed class DeliveryDispatchWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<DeliveryDispatchWorker> logger) : BackgroundService
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
                var coordinator = scope.ServiceProvider.GetRequiredService<DeliveryDispatchCoordinator>();
                await coordinator.DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "A Delivery dispatch pass failed.");
            }
        }
    }
}
