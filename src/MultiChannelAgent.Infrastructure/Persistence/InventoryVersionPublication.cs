using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace MultiChannelAgent.Infrastructure.Persistence;

/// <summary>
/// The one statement that publishes an Inventory's change version, written once per provider so the
/// synchronous and asynchronous halves of <see cref="MultiChannelAgentDbContext"/> cannot drift apart
/// into two different publication semantics.
///
/// It is a single upsert rather than an update followed by a guarded insert, and that is the whole
/// point of the type. "Update; if nothing was there, insert" is check-then-act: two transactions
/// publishing for the same Inventory that has no version row - the residue a rolling deployment can
/// leave behind, since an instance running the previous build creates Inventories the backfill has
/// already passed - can both see nothing to update and both go on to insert the same primary key. One
/// commits, the other dies on the duplicate, and the write it was announcing - a perfectly valid,
/// fully audited change - is rolled back with it. Widening the catch or retrying the caller's mutation
/// would only be tidier ways of losing that race; not entering it is the fix.
/// </summary>
public static class InventoryVersionPublication
{
    /// <summary>
    /// One statement, and one lock decision. <c>HOLDLOCK</c> (serializable) makes the MERGE take a
    /// range lock over the key it is about to decide about, held to commit, so a second transaction
    /// arriving at the same Inventory waits there and then finds the row and increments it. Without
    /// the hint, MERGE under read committed is check-then-insert with nicer syntax and races exactly
    /// as the two statements it replaced did.
    ///
    /// The lock is on the version row's key alone and is taken as the last thing the transaction does
    /// (see <see cref="MultiChannelAgentDbContext.SaveChangesAsync"/>), so this is still the shortest
    /// hold the design can have - it is not a serialization point for the work that came before it.
    /// </summary>
    private const string SqlServerUpsert =
        """
        MERGE INTO InventoryVersions WITH (HOLDLOCK) AS existing
        USING (VALUES ({0})) AS published (InventoryId)
        ON existing.InventoryId = published.InventoryId
        WHEN MATCHED THEN UPDATE SET Version = existing.Version + 1
        WHEN NOT MATCHED THEN INSERT (InventoryId, Version) VALUES (published.InventoryId, 1);
        """;

    /// <summary>
    /// SQLite's own upsert. The conflict target is the primary key the version row is keyed by, and
    /// the unqualified <c>InventoryVersions.Version</c> on the right is the stored value rather than
    /// the one this statement proposed, so a row that already exists is incremented exactly once.
    /// </summary>
    private const string SqliteUpsert =
        """
        INSERT INTO InventoryVersions (InventoryId, Version) VALUES ({0}, 1)
        ON CONFLICT (InventoryId) DO UPDATE SET Version = InventoryVersions.Version + 1;
        """;

    /// <summary>
    /// The publication statement for one Inventory on whichever provider is in use, with the Inventory
    /// carried as a parameter rather than spliced into the text. SQL Server is the production
    /// provider; the other branch is the SQLite the tests run on, matching how this codebase already
    /// chooses an ordinal collation.
    /// </summary>
    public static FormattableString Statement(DatabaseFacade database, Guid inventoryId)
    {
        ArgumentNullException.ThrowIfNull(database);

        return FormattableStringFactory.Create(
            database.IsSqlServer() ? SqlServerUpsert : SqliteUpsert, inventoryId);
    }
}
