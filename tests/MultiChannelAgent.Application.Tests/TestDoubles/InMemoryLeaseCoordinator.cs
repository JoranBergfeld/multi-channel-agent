using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Application.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="ILeaseCoordinator"/> for Application-layer unit tests. Mirrors the
/// exclusivity semantics the SQL-backed implementation must provide: only one owner holds a named
/// lease at a time, and an expired lease becomes acquirable again.
/// </summary>
public sealed class InMemoryLeaseCoordinator(TimeProvider timeProvider) : ILeaseCoordinator
{
    private readonly Dictionary<string, (string OwnerId, DateTimeOffset ExpiresAt)> _leases = [];

    public Task<ILeaseHandle?> TryAcquireAsync(string leaseName, string ownerId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (_leases.TryGetValue(leaseName, out var existing) && existing.ExpiresAt > now)
        {
            return Task.FromResult<ILeaseHandle?>(null);
        }

        _leases[leaseName] = (ownerId, now + duration);
        return Task.FromResult<ILeaseHandle?>(new Handle(this, leaseName, ownerId));
    }

    private sealed class Handle(InMemoryLeaseCoordinator owner, string leaseName, string ownerId) : ILeaseHandle
    {
        public string LeaseName => leaseName;

        public string OwnerId => ownerId;

        public ValueTask DisposeAsync()
        {
            owner._leases.Remove(leaseName);
            return ValueTask.CompletedTask;
        }
    }
}
