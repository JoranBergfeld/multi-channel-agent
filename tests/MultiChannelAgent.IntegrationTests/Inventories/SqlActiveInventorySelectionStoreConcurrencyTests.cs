using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Fast, Docker-free regression coverage for the durable-acceptance race behind
/// <see cref="SqlActiveInventorySelectionStore"/>: two independent <see cref="MultiChannelAgentDbContext"/>
/// instances - each its own real SQLite connection into one shared-cache in-memory database, mirroring
/// two separate HTTP request scopes (for example, bootstrap auto-selection racing an explicit
/// multi-tab selection, or two browser tabs selecting concurrently) - both attempt to upsert the
/// Active Inventory selection for the SAME (ParticipantId, ChannelConversationId) for the first time.
/// A real relational engine (not mocks, not the EF Core InMemory provider, neither of which enforces
/// the primary key this bug depends on) guarantees exactly one insert wins;
/// <see cref="SqlActiveInventorySelectionStore.UpsertAsync"/> must resolve the loser by converging on
/// one row - last write wins - instead of letting a bare <see cref="DbUpdateException"/> escape to the
/// Application boundary as an unhandled 500. This complements the real SQL Server Testcontainers
/// coverage in <see cref="InventorySelectionConcurrencyTests"/>, which proves the identical invariant
/// against the real production provider end-to-end through the HTTP selection endpoint.
/// </summary>
public sealed class SqlActiveInventorySelectionStoreConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlActiveInventorySelectionStoreConcurrencyTests()
    {
        // A shared-cache in-memory SQLite database only persists while at least one connection to it
        // remains open; this connection is kept open for the whole test purely to keep the database
        // alive, while the two stores under test each open and use their own independent connection -
        // exactly as two separate DI scopes each resolve their own DbContext in production.
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    private async Task<(Guid OwnerId, Guid InventoryAId, Guid InventoryBId)> SeedOwnerAndTwoInventoriesAsync()
    {
        var ownerId = Guid.NewGuid();
        var inventoryAId = Guid.NewGuid();
        var inventoryBId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var db = CreateContext();
        db.Participants.Add(new ParticipantEntity
        {
            Id = ownerId,
            DisplayName = "Race Owner",
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryAId,
            Name = "Warehouse A",
            NormalizedName = "warehouse a",
            CreatedByParticipantId = ownerId,
            ClientRequestId = "seed-a",
            CreatedAt = now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = inventoryBId,
            Name = "Warehouse B",
            NormalizedName = "warehouse b",
            CreatedByParticipantId = ownerId,
            ClientRequestId = "seed-b",
            CreatedAt = now,
        });
        await db.SaveChangesAsync(CancellationToken.None);

        return (ownerId, inventoryAId, inventoryBId);
    }

    [Fact]
    public async Task Two_concurrent_first_time_selections_for_the_same_participant_and_conversation_converge_on_one_row()
    {
        var (ownerId, inventoryAId, inventoryBId) = await SeedOwnerAndTwoInventoriesAsync();
        var participantId = new ParticipantId(ownerId);
        const string conversationId = "conversation-race-1";
        var now = DateTimeOffset.UtcNow;

        var selectionA = new ActiveInventorySelection(participantId, conversationId, new InventoryId(inventoryAId), now);
        var selectionB = new ActiveInventorySelection(participantId, conversationId, new InventoryId(inventoryBId), now);

        // A genuine race requires both attempts to observe absence via their check-read before
        // either commits its insert; real SQLite I/O on an uncontended in-memory database usually
        // completes each attempt's check-then-insert sequence before the other one starts, which
        // would never exercise the bug. This 2-party barrier forces both connections' initial
        // check-read (the sole point at which each observes "no row yet") to land at the same
        // instant every run, deterministically reproducing the two-concurrent-HTTP-requests race
        // this store must survive - without relying on incidental thread-scheduling luck.
        using var checkArrivalBarrier = new Barrier(2);
        using var dbA = CreateContext(new SynchronizeFirstReadInterceptor(checkArrivalBarrier));
        using var dbB = CreateContext(new SynchronizeFirstReadInterceptor(checkArrivalBarrier));

        var storeA = new SqlActiveInventorySelectionStore(dbA);
        var storeB = new SqlActiveInventorySelectionStore(dbB);

        var taskA = Task.Run(() => storeA.UpsertAsync(selectionA, CancellationToken.None));
        var taskB = Task.Run(() => storeB.UpsertAsync(selectionB, CancellationToken.None));

        // Neither concurrent attempt may surface the underlying primary-key race as an unhandled
        // DbUpdateException - both must complete normally, converging on one row.
        await Task.WhenAll(taskA, taskB);

        using var verifyDb = CreateContext();
        var rows = await verifyDb.ActiveInventorySelections
            .AsNoTracking()
            .Where(e => e.ParticipantId == ownerId && e.ChannelConversationId == conversationId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Contains(rows.Single().InventoryId, new[] { inventoryAId, inventoryBId });
    }

    /// <summary>
    /// Pauses the very first read against <c>ActiveInventorySelections</c> issued through this
    /// interceptor's <see cref="MultiChannelAgentDbContext"/> until a second participant (the other
    /// concurrent attempt's own interceptor instance, sharing the same <see cref="Barrier"/>) reaches
    /// the same point - forcing two independent check-then-insert sequences to observe "no row yet"
    /// simultaneously. Only the first such read per context instance is gated: a later re-read (for
    /// example the fix's post-conflict convergence re-read) must proceed immediately, since only one
    /// side of the race reaches that second read.
    /// </summary>
    private sealed class SynchronizeFirstReadInterceptor(Barrier checkArrivalBarrier) : DbCommandInterceptor
    {
        private bool _synchronized;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!_synchronized && command.CommandText.Contains("ActiveInventorySelections", StringComparison.Ordinal))
            {
                _synchronized = true;
                await Task.Run(() => checkArrivalBarrier.SignalAndWait(cancellationToken), cancellationToken);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task A_conflict_that_is_not_a_selection_race_still_propagates()
    {
        var (ownerId, _, _) = await SeedOwnerAndTwoInventoriesAsync();
        var participantId = new ParticipantId(ownerId);
        var now = DateTimeOffset.UtcNow;

        // A non-existent InventoryId violates the foreign key on insert - a genuine, unrelated
        // database failure, not the duplicate-first-selection race UpsertAsync is designed to
        // absorb - so it must propagate untouched rather than be disguised as a converged selection.
        var selection = new ActiveInventorySelection(participantId, "conversation-unrelated-1", new InventoryId(Guid.NewGuid()), now);

        using var db = CreateContext();
        var store = new SqlActiveInventorySelectionStore(db);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.UpsertAsync(selection, CancellationToken.None));
    }

    private MultiChannelAgentDbContext CreateContext(DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlite(_connectionString);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new MultiChannelAgentDbContext(builder.Options);
    }
}
