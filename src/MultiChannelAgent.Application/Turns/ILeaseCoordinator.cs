namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// A held distributed lease granting exclusive, time-bounded ownership of a named coordination
/// resource (e.g. "turn-processing"). Disposing releases the lease early; letting it expire is safe.
/// </summary>
public interface ILeaseHandle : IAsyncDisposable
{
    string LeaseName { get; }

    string OwnerId { get; }
}

/// <summary>
/// Coordinates exclusive access to hosted-worker duties across replicas via durable SQL leases,
/// rather than relying on replica affinity.
/// </summary>
public interface ILeaseCoordinator
{
    Task<ILeaseHandle?> TryAcquireAsync(string leaseName, string ownerId, TimeSpan duration, CancellationToken cancellationToken);
}
