namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// A durable, named lease row granting one owner exclusive, time-bounded coordination rights (e.g.
/// "turn-processing"). Coordinates hosted workers across replicas through SQL rather than replica
/// affinity, and survives replica restarts.
/// </summary>
public sealed class LeaseEntity
{
    public required string LeaseName { get; set; }

    public required string OwnerId { get; set; }

    public DateTimeOffset AcquiredAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
