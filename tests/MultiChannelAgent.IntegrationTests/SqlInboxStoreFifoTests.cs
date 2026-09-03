using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage (a real relational engine, not mocks) of the durable per-conversation
/// FIFO order <see cref="SqlInboxStore"/> owns: acceptance assigns a durable monotonic
/// per-ChannelConversation sequence, and claiming only ever yields a conversation's head - the
/// earliest accepted Turn that has not completed - so no later Turn in a conversation can be claimed
/// or processed while an earlier one is still outstanding, whatever the batch limit, pass count, or
/// lease boundary. Received timestamps deliberately collide here: wall-clock time is not an ordering
/// key, and ordering by it alone is exactly the bug this covers.
/// </summary>
public sealed class SqlInboxStoreFifoTests : IDisposable
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTimeOffset SameInstant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlInboxStoreFifoTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    [Fact]
    public async Task Turns_accepted_at_the_very_same_instant_still_get_a_strictly_increasing_conversation_order()
    {
        using var db = CreateContext();
        var store = new SqlInboxStore(db);

        var first = await AcceptAsync(store, "native-1", "conversation-1", SameInstant);
        var second = await AcceptAsync(store, "native-2", "conversation-1", SameInstant);
        var third = await AcceptAsync(store, "native-3", "conversation-1", SameInstant);

        using var verifyDb = CreateContext();
        var sequences = await verifyDb.InboxEntries.AsNoTracking()
            .Where(e => e.ChannelConversationId == "conversation-1")
            .OrderBy(e => e.ConversationSequence)
            .Select(e => new { e.TurnId, e.ConversationSequence })
            .ToListAsync();

        Assert.Equal(new[] { first.Value, second.Value, third.Value }, sequences.Select(s => s.TurnId));
        Assert.Equal(sequences.Select(s => s.ConversationSequence).OrderBy(s => s), sequences.Select(s => s.ConversationSequence));
        Assert.Equal(3, sequences.Select(s => s.ConversationSequence).Distinct().Count());
    }

    // The claim itself - not a downstream in-memory guard - is what makes FIFO unbreakable: a
    // conversation offers exactly its head, so even a batch limit far larger than the backlog can
    // never hand a worker a Turn whose predecessor is still outstanding.
    [Fact]
    public async Task Claiming_only_ever_offers_each_conversations_head_even_when_the_batch_limit_is_generous()
    {
        using var db = CreateContext();
        var store = new SqlInboxStore(db);

        var firstInA = await AcceptAsync(store, "native-a1", "conversation-a", SameInstant);
        await AcceptAsync(store, "native-a2", "conversation-a", SameInstant);
        await AcceptAsync(store, "native-a3", "conversation-a", SameInstant);
        var firstInB = await AcceptAsync(store, "native-b1", "conversation-b", SameInstant);
        await AcceptAsync(store, "native-b2", "conversation-b", SameInstant);

        var claimed = await store.ClaimPendingAsync(50, CancellationToken.None);

        Assert.Equal([firstInA, firstInB], claimed.Select(t => t.TurnId).OrderBy(t => t == firstInA ? 0 : 1));
        Assert.Equal(2, claimed.Count);
    }

    [Fact]
    public async Task A_conversations_successor_is_only_offered_once_its_predecessor_has_completed()
    {
        using var db = CreateContext();
        var store = new SqlInboxStore(db);

        var first = await AcceptAsync(store, "native-1", "conversation-1", SameInstant);
        var second = await AcceptAsync(store, "native-2", "conversation-1", SameInstant);

        // Several claim passes (each standing in for a separate lease acquisition) never advance
        // past the head while it is still outstanding.
        for (var pass = 0; pass < 3; pass++)
        {
            var stillHead = await store.ClaimPendingAsync(50, CancellationToken.None);
            Assert.Equal(first, Assert.Single(stillHead).TurnId);
        }

        await CompleteAsync(first);

        var next = await store.ClaimPendingAsync(50, CancellationToken.None);
        Assert.Equal(second, Assert.Single(next).TurnId);
    }

    // Two concurrent acceptances in one conversation must not collide on the same order key: the
    // durable sequence is the tie-break FIFO depends on, so a race that produced a duplicate (or an
    // exception) would break ordering outright.
    [Fact]
    public async Task Two_concurrent_acceptances_in_one_conversation_get_distinct_sequences()
    {
        using var dbA = CreateContext();
        using var dbB = CreateContext();
        var storeA = new SqlInboxStore(dbA);
        var storeB = new SqlInboxStore(dbB);

        var taskA = storeA.AcceptAsync(
            InboundTurn.Create("native-race-a", SomeParticipant, "conversation-race", "a", null, SameInstant, null), CancellationToken.None);
        var taskB = storeB.AcceptAsync(
            InboundTurn.Create("native-race-b", SomeParticipant, "conversation-race", "b", null, SameInstant, null), CancellationToken.None);

        await Task.WhenAll(taskA, taskB);

        using var verifyDb = CreateContext();
        var sequences = await verifyDb.InboxEntries.AsNoTracking()
            .Where(e => e.ChannelConversationId == "conversation-race")
            .Select(e => e.ConversationSequence)
            .ToListAsync();

        Assert.Equal(2, sequences.Count);
        Assert.Equal(2, sequences.Distinct().Count());
    }

    private static async Task<TurnId> AcceptAsync(SqlInboxStore store, string nativeMessageId, string conversationId, DateTimeOffset receivedAt)
    {
        var result = await store.AcceptAsync(
            InboundTurn.Create(nativeMessageId, SomeParticipant, conversationId, "hello", null, receivedAt, null), CancellationToken.None);
        return result.Turn.TurnId;
    }

    private async Task CompleteAsync(TurnId turnId)
    {
        using var db = CreateContext();
        var entry = await db.InboxEntries.SingleAsync(e => e.TurnId == turnId.Value);
        entry.Status = Infrastructure.Persistence.Entities.InboxEntryStatus.Completed;
        await db.SaveChangesAsync();
    }

    private MultiChannelAgentDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MultiChannelAgentDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new MultiChannelAgentDbContext(options);
    }
}
