using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The one interleaving that decides whether "New conversation" really clears pending confirmation
/// state, proven on real SQL Server with <c>READ_COMMITTED_SNAPSHOT</c> on - the setting Azure SQL
/// databases are created with, and the setting under which the obvious implementation is silently
/// wrong.
///
/// Write U for the rotation's proposal-settle statement, P for the instant the Turn's proposal becomes
/// durable, S for the post-dispatch supersession check, and R for the rotation's commit. The window
/// driven here is <b>U &lt; P &lt; S &lt; R</b>: the reset settles nothing because the Turn has not
/// proposed yet, the Turn then proposes, and the check runs while the reset is still committing. Under
/// RCSI an ordinary read answers that check from the generation the reset is replacing - asserted
/// below as the control - so neither mechanism settles anything and a confirmable proposal is left in
/// a conversation the Participant has left.
///
/// The fix is that the check reads through a seam that takes the binding row's lock, so it and the
/// rotation are strictly ordered. Both halves are asserted: the check cannot answer while the reset
/// holds the row, and once the reset commits the check reads the new generation and settles the
/// proposal.
/// </summary>
public sealed class SqlSupersessionReadSerializationTests : SqlIntegrationTestBase
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ChannelConversationId Conversation = new("web:profile-1");
    private static readonly InventoryId SomeInventory = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly UnitId EachUnit = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    /// <summary>
    /// How long the supersession check is watched for while the reset holds the binding row. A read
    /// that takes the lock cannot finish at all until the reset ends, so any value proves the point;
    /// this one only bounds how long a regression takes to report itself.
    /// </summary>
    private static readonly TimeSpan BlockedWindow = TimeSpan.FromSeconds(2);

    /// <summary>The bound on waiting for the other side to reach its own point, generous because it covers container-speed SQL.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

    [SkippableFact]
    public async Task A_reset_still_committing_while_a_Turn_stores_its_proposal_leaves_nothing_confirmable()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed supersession serialization proof.");

        var connectionString = ConnectionString();
        await EnableReadCommittedSnapshotAsync(connectionString);
        await SeedAsync();

        using (var db = Context(connectionString))
        {
            var binding = await new SqlFoundryConversationBindingStore(db)
                .GetOrCreateAsync(Participant, Conversation, Now, CancellationToken.None);

            Assert.Equal(1, binding.Generation);
        }

        var reachedTheGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The reset, paused after it has bumped the generation and settled what was pending - nothing -
        // and before it commits.
        var rotating = Task.Run(async () =>
        {
            using var db = Context(
                connectionString, new PauseAfterInterceptor("[ConfirmationProposals]", reachedTheGate, release.Task));

            return await new SqlConversationRotationStore(db, new SqlFoundryConversationBindingStore(db))
                .RotateAsync(Participant, Conversation, Now.AddMinutes(1), CancellationToken.None);
        });

        await reachedTheGate.Task.WaitAsync(GateTimeout);

        // P: the queued Turn, accepted under generation 1, stores the proposal the reset never saw.
        // Bounded, so an orchestration that cannot reach P - because the paused reset blocked it -
        // reports a failure rather than hanging the run.
        var proposal = StockProposal();
        await Task.Run(async () =>
        {
            using var db = Context(connectionString);
            await new SqlConfirmationProposalStore(db).StoreAsync(proposal, Now.AddMinutes(2), CancellationToken.None);
        }).WaitAsync(GateTimeout);

        // The control this whole test rests on: with RCSI on, an ordinary read is still served the
        // generation the reset is replacing. Answering the supersession question from this is exactly
        // the defect - and it is invisible on a database without RCSI, which is why this runs here.
        var snapshotGeneration = await Task.Run(async () =>
        {
            using var db = Context(connectionString);
            var snapshot = await new SqlFoundryConversationBindingStore(db)
                .GetOrCreateAsync(Participant, Conversation, Now.AddMinutes(3), CancellationToken.None);

            return snapshot.Generation;
        }).WaitAsync(GateTimeout);

        Assert.Equal(1, snapshotGeneration);

        // S: the post-dispatch check, run through the seam that takes the row's lock.
        using var settleDb = Context(connectionString);
        var settling = Task.Run(() => new ConfirmationProposalLifecycle(
                new SqlConfirmationProposalStore(settleDb), new SqlFoundryConversationBindingStore(settleDb))
            .SettleSupersededConversationAsync(AcceptedUnderGenerationOne(), Now.AddMinutes(4), CancellationToken.None));

        // It must not be able to answer at all while the reset holds the binding row. An unhinted read
        // would have answered here, from the stale generation, and settled nothing.
        await Assert.ThrowsAsync<TimeoutException>(() => settling.WaitAsync(BlockedWindow));

        // And it must be waiting on that row rather than merely slow to start: a check that had not
        // yet reached the database would time out above for a reason that proves nothing.
        Assert.True(
            await SomeRequestIsBlockedAsync(connectionString),
            "The supersession check was not blocked on the reset's binding row, so this run proved nothing.");

        release.TrySetResult();

        var rotated = await rotating.WaitAsync(GateTimeout);
        Assert.Equal(2, rotated.Binding.Generation);

        // The hole, stated: the reset itself settled nothing, because at U the proposal did not exist.
        Assert.False(rotated.ClearedPendingConfirmation);

        var settlement = await settling.WaitAsync(GateTimeout);
        Assert.True(settlement.ConversationWasSuperseded);
        Assert.Equal(ProposalStatus.ConversationReset, settlement.Settled);

        using var verifyDb = Context(connectionString);
        var stored = await verifyDb.ConfirmationProposals.AsNoTracking()
            .SingleAsync(p => p.ProposalId == proposal.Id.Value);

        Assert.Equal(nameof(ProposalStatus.ConversationReset), stored.Status);
        Assert.Null(await new SqlConfirmationProposalStore(verifyDb)
            .FindPendingAsync(Participant, Conversation.Value, CancellationToken.None));
    }

    private string ConnectionString()
    {
        using var scope = Factory!.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>().Database.GetConnectionString()!;
    }

    /// <summary>
    /// Puts the test database in the mode Azure SQL databases are created in. It is done against
    /// <c>master</c> with <c>ROLLBACK IMMEDIATE</c> because the option cannot be set while other
    /// sessions hold the database, and the pool is cleared afterwards so nothing reuses a connection
    /// that was closed out from under it. The setting is then read back: a test that silently ran
    /// without it would prove nothing at all.
    /// </summary>
    private static async Task EnableReadCommittedSnapshotAsync(string connectionString)
    {
        var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var masterConnectionString = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }
            .ConnectionString;

        await using (var master = new SqlConnection(masterConnectionString))
        {
            await master.OpenAsync();

            // The database name cannot be a parameter in ALTER DATABASE, so it is quoted through
            // QUOTENAME rather than concatenated, and it is a container name this test itself created.
            await using var alter = master.CreateCommand();
            alter.CommandText =
                "DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@database) + " +
                "N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE'; EXEC sp_executesql @sql;";
            alter.Parameters.AddWithValue("@database", databaseName);
            await alter.ExecuteNonQueryAsync();
        }

        SqlConnection.ClearAllPools();

        await using var verify = new SqlConnection(masterConnectionString);
        await verify.OpenAsync();
        await using var query = verify.CreateCommand();
        query.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = @database";
        query.Parameters.AddWithValue("@database", databaseName);

        Assert.True((bool)(await query.ExecuteScalarAsync())!);
    }

    private static MultiChannelAgentDbContext Context(string connectionString, DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>().UseSqlServer(connectionString);

        return new MultiChannelAgentDbContext(
            interceptor is null ? options.Options : options.AddInterceptors(interceptor).Options);
    }

    /// <summary>
    /// Whether any request against this database is currently waiting on another session's lock. It
    /// is what turns "the check did not finish in time" into "the check is queued behind the reset",
    /// which is the only reading of that timeout worth asserting.
    /// </summary>
    private static async Task<bool> SomeRequestIsBlockedAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.dm_exec_requests " +
            "WHERE blocking_session_id <> 0 AND database_id = DB_ID()";

        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

    /// <summary>The trusted context of a Turn accepted under generation 1 - the one this reset leaves behind.</summary>
    private static TurnExecutionContext AcceptedUnderGenerationOne() => new(
        TurnId.NewId(),
        Participant,
        Conversation,
        new FoundryConversationId(Guid.NewGuid()),
        FoundryConversationGeneration: 1,
        SomeInventory,
        TraceId: null);

    private async Task SeedAsync()
    {
        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        db.Participants.Add(new ParticipantEntity
        {
            Id = Participant.Value,
            DisplayName = "Resetting Participant",
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        db.Inventories.Add(new InventoryEntity
        {
            Id = SomeInventory.Value,
            Name = "Warehouse",
            NormalizedName = "warehouse",
            CreatedByParticipantId = Participant.Value,
            ClientRequestId = "seed-1",
            CreatedAt = Now,
        });
        db.Memberships.Add(new MembershipEntity
        {
            InventoryId = SomeInventory.Value,
            ParticipantId = Participant.Value,
            Role = MembershipRole.Owner,
            CreatedAt = Now,
        });
        db.Units.Add(new UnitEntity
        {
            Id = EachUnit.Value,
            InventoryId = SomeInventory.Value,
            CanonicalName = "each",
            NormalizedCanonicalName = "each",
            IsReserved = true,
            ConcurrencyStamp = Guid.NewGuid(),
            CreatedAt = Now,
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static ConfirmationProposal StockProposal()
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Participant,
            Conversation.Value,
            SomeInventory,
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", EachUnit, "each",
                        LocationId: null, LocationName: null, Note: null,
                        Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    /// <summary>
    /// Holds a transaction open at the point just after one named table was written, so the test can
    /// do its own work while that transaction's locks are still held. It pauses after the statement
    /// executed rather than before, which is what puts the reset between its own settle and its
    /// commit.
    /// </summary>
    private sealed class PauseAfterInterceptor(string table, TaskCompletionSource reached, Task release)
        : DbCommandInterceptor
    {
        private bool _paused;

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!_paused && command.CommandText.Contains(table, StringComparison.Ordinal))
            {
                _paused = true;
                reached.TrySetResult();
                await release;
            }

            return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }
    }
}
