using System.Data;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.Infrastructure.Turns;

/// <summary>
/// SQL Server-backed exclusive lease coordination. Acquisition runs inside a serializable
/// transaction so concurrent hosted-worker replicas cannot both observe a lease as free and both
/// acquire it.
/// </summary>
public sealed class SqlLeaseCoordinator(MultiChannelAgentDbContext db, TimeProvider timeProvider) : ILeaseCoordinator
{
    public async Task<ILeaseHandle?> TryAcquireAsync(string leaseName, string ownerId, TimeSpan duration, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var existing = await db.Leases.FirstOrDefaultAsync(l => l.LeaseName == leaseName, cancellationToken);

        if (existing is not null && existing.ExpiresAt > now)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        if (existing is null)
        {
            db.Leases.Add(new LeaseEntity
            {
                LeaseName = leaseName,
                OwnerId = ownerId,
                AcquiredAt = now,
                ExpiresAt = now + duration,
            });
        }
        else
        {
            existing.OwnerId = ownerId;
            existing.AcquiredAt = now;
            existing.ExpiresAt = now + duration;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new Handle(this, leaseName, ownerId);
    }

    private async Task ReleaseAsync(string leaseName, string ownerId)
    {
        var entity = await db.Leases.FirstOrDefaultAsync(l => l.LeaseName == leaseName && l.OwnerId == ownerId);
        if (entity is not null)
        {
            db.Leases.Remove(entity);
            await db.SaveChangesAsync();
        }
    }

    private sealed class Handle(SqlLeaseCoordinator owner, string leaseName, string ownerId) : ILeaseHandle
    {
        public string LeaseName => leaseName;

        public string OwnerId => ownerId;

        public async ValueTask DisposeAsync() => await owner.ReleaseAsync(leaseName, ownerId);
    }
}
