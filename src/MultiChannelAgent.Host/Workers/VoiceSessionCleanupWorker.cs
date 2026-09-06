using MultiChannelAgent.Application.Voice;

namespace MultiChannelAgent.Host.Workers;

/// <summary>
/// Periodically drives <see cref="VoiceSessionCleanupCoordinator.CleanupAsync"/>, so expired, idle,
/// and stale-owner voice sessions are force-closed and their capacity reclaimed promptly. Avoids
/// overlapping passes with a single <see cref="PeriodicTimer"/>.
/// </summary>
public sealed class VoiceSessionCleanupWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<VoiceSessionCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<VoiceSessionCleanupCoordinator>();
                await coordinator.CleanupAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "A voice session cleanup pass failed.");
            }
        }
    }
}
