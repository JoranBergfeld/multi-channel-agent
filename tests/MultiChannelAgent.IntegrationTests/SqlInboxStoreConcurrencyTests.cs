using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free regression coverage for the durable-acceptance race behind
/// <see cref="SqlInboxStore"/>: two independent <see cref="MultiChannelAgentDbContext"/> instances -
/// each its own real SQLite connection into one shared-cache in-memory database, mirroring two
/// separate HTTP request scopes hitting one real SQL Server - both attempt to accept a Turn for the
/// SAME <c>NativeMessageId</c> concurrently. A real relational engine (not mocks, not the EF Core
/// InMemory provider, neither of which enforces the unique index this bug depends on) guarantees
/// exactly one insert wins; <see cref="SqlInboxStore.AcceptAsync"/> must resolve the loser into the
/// winner's Turn instead of letting a bare <see cref="DbUpdateException"/> escape to the Application
/// boundary as an unhandled 500. This complements the real SQL Server Testcontainers coverage in
/// <see cref="TurnAcceptanceConcurrencyTests"/>, which proves the identical invariant against the real
/// production provider end-to-end through <see cref="Application.Turns.TurnAcceptanceService"/>.
/// </summary>
public sealed class SqlInboxStoreConcurrencyTests : IDisposable
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlInboxStoreConcurrencyTests()
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

    [Fact]
    public async Task Two_concurrent_acceptance_attempts_for_the_same_native_message_id_converge_on_one_turn()
    {
        const string nativeMessageId = "native-race-1";
        var now = DateTimeOffset.UtcNow;
        var turnA = InboundTurn.Create(nativeMessageId, SomeParticipant, "conversation-race-1", "hello a", null, now, null);
        var turnB = InboundTurn.Create(nativeMessageId, SomeParticipant, "conversation-race-1", "hello b", null, now, null);

        using var dbA = CreateContext();
        using var dbB = CreateContext();

        var storeA = new SqlInboxStore(dbA);
        var storeB = new SqlInboxStore(dbB);

        var taskA = storeA.AcceptAsync(turnA, CancellationToken.None);
        var taskB = storeB.AcceptAsync(turnB, CancellationToken.None);

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(results[0].Turn.TurnId, results[1].Turn.TurnId);
        Assert.Single(results, r => !r.WasAlreadyAccepted);
        Assert.Single(results, r => r.WasAlreadyAccepted);

        using var verifyDb = CreateContext();
        var rows = await verifyDb.InboxEntries.AsNoTracking()
            .Where(e => e.NativeMessageId == nativeMessageId)
            .ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task A_conflict_that_is_not_a_duplicate_native_message_id_still_propagates()
    {
        var now = DateTimeOffset.UtcNow;
        var seededTurn = InboundTurn.Create("native-unrelated-a", SomeParticipant, "conversation-unrelated-a", "hello a", null, now, null);

        using (var seedDb = CreateContext())
        {
            await new SqlInboxStore(seedDb).AcceptAsync(seededTurn, CancellationToken.None);
        }

        // Collides on the PRIMARY KEY (TurnId) but has a completely different NativeMessageId: a
        // genuine, unrelated database failure - not the duplicate-delivery race AcceptAsync is
        // designed to absorb - so it must propagate untouched rather than be reported as a duplicate.
        var conflictingTurn = new InboundTurn
        {
            TurnId = seededTurn.TurnId,
            NativeMessageId = "native-unrelated-b",
            ParticipantId = SomeParticipant,
            ChannelConversationId = new ChannelConversationId("conversation-unrelated-b"),
            ContentText = "hello b",
            ReceivedAt = now,
        };

        using var db = CreateContext();
        var store = new SqlInboxStore(db);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.AcceptAsync(conflictingTurn, CancellationToken.None));
    }

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }
}
