using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="TurnProgressEventCleanupCoordinator.CleanupAsync"/>, so retained
/// progress markers age out on the same retention window as Outcome payloads instead of
/// accumulating forever. Only progress markers are swept; the Turn, its Outcome, and any Deliveries
/// are untouched.
/// </summary>
public sealed class TurnProgressEventCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<TurnProgressEventCleanupWorker> logger) : BackgroundService
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
                var coordinator = scope.ServiceProvider.GetRequiredService<TurnProgressEventCleanupCoordinator>();
                await coordinator.CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "A Turn progress cleanup pass failed.");
            }
        }
    }
}
