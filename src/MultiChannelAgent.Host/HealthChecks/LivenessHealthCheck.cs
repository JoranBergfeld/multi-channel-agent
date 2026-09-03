using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MultiChannelAgent.Host.HealthChecks;

/// <summary>
/// Liveness has no external dependencies: it only reports whether the process itself is running and
/// able to serve requests, so replica restarts are triggered on process-level failure, not on
/// transient dependency outages (that is what readiness is for).
/// </summary>
public sealed class LivenessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("The application is running."));
}
