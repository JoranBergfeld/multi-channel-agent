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
        var ticks = cutoff.UtcTicks;

        // Bounded on when the fact occurred rather than on the mirrored expiry column, because
        // retention is stated in terms of the fact's own age, and the age is the authority.
        //
        // The bounded set is selected first so one sweep can never turn into an unbounded delete, and
        // the oldest facts are always the ones taken. The predicate compares the mirrored ticks, not
        // the DateTimeOffset: SQLite cannot translate a DateTimeOffset comparison at all, and a
        // ninety-day rule that only runs on one engine is not one this system can claim to enforce.
        var deletable = await db.InventoryAudits
            .AsNoTracking()
            .Where(a => a.OccurredAtUtcTicks < ticks)
            .OrderBy(a => a.OccurredAtUtcTicks)
            .Take(maxRows)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        return deletable.Count == 0
            ? 0
            : await db.InventoryAudits.Where(a => deletable.Contains(a.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
