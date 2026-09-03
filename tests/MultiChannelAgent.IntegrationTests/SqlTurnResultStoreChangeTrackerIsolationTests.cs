using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.Infrastructure.Turns;
using Xunit;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free regression coverage for the EF Core <c>ChangeTracker</c> contamination
/// invariant behind <see cref="SqlTurnResultStore"/>: a real relational engine (SQLite, with foreign
/// keys enabled) is used - rather than mocks or the EF Core InMemory provider, neither of which
/// enforces the foreign-key violation this bug depends on - to reproduce, in a single shared
/// <see cref="MultiChannelAgentDbContext"/>, exactly the failure mode
/// <see cref="TurnProcessingCoordinator"/> exercises in production: one scoped context processes a
/// whole batch of Turns, so a failed <c>SaveChangesAsync</c> for Turn A must not leave stale tracked
/// entities that contaminate Turn B's later <c>SaveChangesAsync</c> call in that same scope. This
/// complements <see cref="SqlTurnResultStoreTests"/>, which proves the same invariant against a real
/// SQL Server container but (before this file) only ever used a fresh scope per attempt and so never
/// exercised cross-Turn contamination within one scope.
/// </summary>
public sealed class SqlTurnResultStoreChangeTrackerIsolationTests : IDisposable
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private readonly SqliteConnection _connection;
    private readonly MultiChannelAgentDbContext _db;

    public SqlTurnResultStoreChangeTrackerIsolationTests()
    {
        // An in-memory SQLite database only persists for the lifetime of one open connection, so the
        // connection is kept open for the whole test and shared by every DbContext usage below - just
        // like one coordinator scope shares one DbContext (and one underlying connection) across a
        // whole batch of Turns in production.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new MultiChannelAgentDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_failed_record_attempt_does_not_contaminate_a_later_record_attempt_in_the_same_scope()
    {
        var turnA = SeedPendingInboxEntry("native-a");
        var turnB = SeedPendingInboxEntry("native-b");

        // Reuse the SAME DbContext (and therefore the same ChangeTracker) for both attempts, exactly
        // as TurnProcessingCoordinator does for every Turn in one claimed batch within one DI scope.
        var turnResultStore = new SqlTurnResultStore(_db);

        var now = DateTimeOffset.UtcNow;
        var outcomeA = Outcome.Completed(turnA, "echoed", "Echoed: a", now);
        var validDeliveryA = Delivery.Request(turnA, "synthetic", "Echoed: a", now);

        // A Delivery for a Turn with no InboxEntry row violates the real foreign-key constraint at
        // the database, guaranteeing SaveChangesAsync fails mid-write - after the valid Outcome
        // insert, the valid Delivery insert, and the inbox completion update for Turn A were all
        // already staged in the same unit of work.
        var rogueDelivery = Delivery.Request(TurnId.NewId(), "synthetic", "orphaned", now);

        await Assert.ThrowsAsync<DbUpdateException>(() => turnResultStore.RecordAsync(
            outcomeA,
            [validDeliveryA, rogueDelivery],
            CancellationToken.None));

        // Turn B's record attempt is a completely independent, valid operation. It must succeed even
        // though it runs on the same DbContext right after Turn A's failed attempt.
        var outcomeB = Outcome.Completed(turnB, "echoed", "Echoed: b", now);
        var deliveryB = Delivery.Request(turnB, "synthetic", "Echoed: b", now);

        await turnResultStore.RecordAsync(outcomeB, [deliveryB], CancellationToken.None);

        // Turn A left no partial state: it remains exactly as it was before the failed attempt.
        Assert.Null(await _db.Outcomes.AsNoTracking().FirstOrDefaultAsync(o => o.TurnId == turnA.Value));
        Assert.Empty(await _db.Deliveries.AsNoTracking().Where(d => d.TurnId == turnA.Value).ToListAsync());
        var inboxEntryA = await _db.InboxEntries.AsNoTracking().FirstAsync(e => e.TurnId == turnA.Value);
        Assert.Equal(InboxEntryStatus.Pending, inboxEntryA.Status);

        // Turn B is fully and correctly recorded.
        var savedOutcomeB = await _db.Outcomes.AsNoTracking().FirstAsync(o => o.TurnId == turnB.Value);
        Assert.Equal("echoed", savedOutcomeB.Code);
        var savedDeliveryB = await _db.Deliveries.AsNoTracking().SingleAsync(d => d.TurnId == turnB.Value);
        Assert.Equal(DeliveryEntityStatus.Pending, savedDeliveryB.Status);
        var inboxEntryB = await _db.InboxEntries.AsNoTracking().FirstAsync(e => e.TurnId == turnB.Value);
        Assert.Equal(InboxEntryStatus.Completed, inboxEntryB.Status);
    }

    private TurnId SeedPendingInboxEntry(string nativeMessageId)
    {
        var turn = InboundTurn.Create(nativeMessageId, SomeParticipant, $"conversation-{nativeMessageId}", "hello", null, DateTimeOffset.UtcNow, null);

        _db.InboxEntries.Add(new InboxEntryEntity
        {
            TurnId = turn.TurnId.Value,
            NativeMessageId = turn.NativeMessageId,
            ParticipantId = turn.ParticipantId.Value,
            ChannelConversationId = turn.ChannelConversationId.Value,
            ContentText = turn.ContentText,
            ReceivedAt = turn.ReceivedAt,
            CreatedAt = turn.ReceivedAt,
            Status = InboxEntryStatus.Pending,
        });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        return turn.TurnId;
    }
}
