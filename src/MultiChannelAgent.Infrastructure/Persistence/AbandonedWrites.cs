using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MultiChannelAgent.Infrastructure.Persistence;

/// <summary>
/// How a store ends a write it cannot finish.
///
/// Both halves matter, and the second is the one that is easy to forget. Rolling the transaction back
/// undoes the database; only clearing the <c>ChangeTracker</c> undoes this process. A
/// <see cref="MultiChannelAgentDbContext"/> is scoped to a whole batch of Turns, so an entity left
/// Added by a failed write waits for the next Turn's <c>SaveChangesAsync</c> and is committed by a
/// Turn that never asked for it - and an entity left Unchanged after its transaction rolled back is a
/// phantom that later reads in that same scope resolve against.
/// </summary>
internal static class AbandonedWrites
{
    /// <summary>
    /// Abandons <paramref name="transaction"/> and everything the attempt staged.
    ///
    /// The tracker is cleared first, so the guarantee holds even if the rollback cannot complete. The
    /// rollback then deliberately does not observe the caller's CancellationToken: cleanup that is
    /// itself cancellable is not cleanup, and cancellation is one of the very faults this runs for.
    /// A rollback that fails is swallowed on purpose - the transaction is already doomed, disposal
    /// will finish with it, and surfacing that secondary error would replace the fault being reported
    /// with one that explains nothing.
    /// </summary>
    public static async Task AbandonAsync(this DbContext db, IDbContextTransaction transaction)
    {
        db.ChangeTracker.Clear();

        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Deliberately ignored; see the remarks above.
        }
    }
}
