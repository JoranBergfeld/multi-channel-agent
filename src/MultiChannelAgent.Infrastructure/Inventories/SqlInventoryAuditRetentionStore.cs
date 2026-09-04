using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// SQL Server-backed <see cref="IInventoryAuditRetentionStore"/>: the bounded delete that finally
/// enforces the ninety days <c>AuditFact.RetentionDays</c> has always declared.
/// </summary>
public sealed class SqlInventoryAuditRetentionStore(MultiChannelAgentDbContext db) : IInventoryAuditRetentionStore
{
    public async Task<int> DeleteOccurredBeforeAsync(DateTimeOffset cutoff, int maxRows, CancellationToken cancellationToken)
    {
        // Bounded on when the fact occurred rather than on the mirrored expiry column, because
        // retention is stated in terms of the fact's own age, and the age is the authority.
        //
        // The bounded set is selected first so one sweep can never turn into an unbounded delete, and
        // the oldest facts are always the ones taken. Unlike this ticket's other sweeps, the
        // predicate compares DateTimeOffset directly rather than a mirrored ticks column: audits
        // predate that convention and carry no such column, so this store is exercised against SQL
        // Server only - SQLite cannot translate a DateTimeOffset comparison at all.
        var deletable = await db.InventoryAudits
            .AsNoTracking()
            .Where(a => a.OccurredAtUtc < cutoff)
            .OrderBy(a => a.OccurredAtUtc)
            .Take(maxRows)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        return deletable.Count == 0
            ? 0
            : await db.InventoryAudits.Where(a => deletable.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
