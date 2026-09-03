using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Fast, Docker-free coverage (a real relational engine, not mocks) of which pending response parts
/// <see cref="SqlDeliveryStore"/> offers for dispatch. Two properties have to hold at once, and a
/// naive ordering satisfies only one of them: within a conversation, one Turn's answer must never be
/// sent before an earlier Turn's, and no conversation may be starved of dispatch by busier or newer
/// ones. Ordering is between Turns only - every producer records exactly one response part per
/// answered Turn, so there is no within-Turn order to guarantee.
/// </summary>
public sealed class SqlDeliveryStoreClaimTests : IDisposable
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTimeOffset SameInstant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _connectionString;

    public SqlDeliveryStoreClaimTests()
    {
        _connectionString = $"DataSource=file:{Guid.NewGuid()}?mode=memory&cache=shared";
        _keepAliveConnection = new SqliteConnection(_connectionString);
        _keepAliveConnection.Open();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _keepAliveConnection.Dispose();

    // Fairness across conversations: a long-running conversation's response part has been waiting
    // longest, but its Turn carries a high conversation sequence purely because that conversation has
    // answered many Turns before. Ordering dispatch by that sequence lets a trickle of brand-new
    // conversations (all at sequence 1) fill every batch and starve it indefinitely.
    [Fact]
    public async Task An_old_response_part_deep_in_its_conversation_is_not_starved_by_newer_conversations()
    {
        using var db = CreateContext();
        var inbox = new SqlInboxStore(db);
        var deliveries = new SqlDeliveryStore(db);

        const string longRunningConversation = "conversation-long-running";
        foreach (var answered in new[] { "native-old-1", "native-old-2", "native-old-3" })
        {
            var completedTurn = await AcceptAsync(inbox, answered, longRunningConversation, SameInstant.AddMinutes(-10));
            await CompleteAsync(completedTurn);
        }

        var oldestWaiting = await RequestDeliveryAsync(
            inbox, deliveries, "native-old-4", longRunningConversation, SameInstant);

        var newerWaiting = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            newerWaiting.Add(await RequestDeliveryAsync(
                inbox, deliveries, $"native-new-{i}", $"conversation-new-{i}", SameInstant.AddSeconds(i)));
        }

        var claimed = await deliveries.ClaimPendingAsync(2, CancellationToken.None);

        Assert.Equal(2, claimed.Count);
        Assert.Equal(oldestWaiting, claimed[0].DeliveryId);
        Assert.Equal(newerWaiting[0], claimed[1].DeliveryId);
    }

    // Ordering between a conversation's Turns cannot rest on wall-clock time: an adapter supplies the
    // instant its channel received a message, so a later Turn can carry an earlier one (a delayed
    // delivery, a replica whose clock lags). Its answer must still never be dispatched ahead of the
    // earlier Turn's, which is only guaranteed by not offering it at all while that one is pending.
    [Fact]
    public async Task A_later_response_part_is_never_offered_while_an_earlier_one_in_its_conversation_waits()
    {
        using var db = CreateContext();
        var inbox = new SqlInboxStore(db);
        var deliveries = new SqlDeliveryStore(db);

        const string conversation = "conversation-skewed";
        var firstAnswer = await RequestDeliveryAsync(inbox, deliveries, "native-first", conversation, SameInstant);
        var secondAnswer = await RequestDeliveryAsync(
            inbox, deliveries, "native-second", conversation, SameInstant.AddMinutes(-5));

        var claimed = await deliveries.ClaimPendingAsync(10, CancellationToken.None);

        Assert.Equal(firstAnswer, Assert.Single(claimed).DeliveryId);
        Assert.DoesNotContain(secondAnswer, claimed.Select(d => d.DeliveryId));
    }

    [Fact]
    public async Task A_conversations_next_response_part_is_offered_once_the_earlier_one_is_delivered()
    {
        using var db = CreateContext();
        var inbox = new SqlInboxStore(db);
        var deliveries = new SqlDeliveryStore(db);

        const string conversation = "conversation-sequential";
        var firstAnswer = await RequestDeliveryAsync(inbox, deliveries, "native-first", conversation, SameInstant);
        var secondAnswer = await RequestDeliveryAsync(inbox, deliveries, "native-second", conversation, SameInstant.AddSeconds(1));

        var first = Assert.Single(await deliveries.ClaimPendingAsync(10, CancellationToken.None));
        Assert.Equal(firstAnswer, first.DeliveryId);

        await deliveries.SaveAsync(first.MarkDelivered(SameInstant.AddSeconds(2)), CancellationToken.None);

        var next = Assert.Single(await deliveries.ClaimPendingAsync(10, CancellationToken.None));
        Assert.Equal(secondAnswer, next.DeliveryId);
    }

    // A failed send leaves the response part pending and still first in line for its conversation, so
    // a retry sends that same part again rather than letting the next one overtake it.
    [Fact]
    public async Task A_failed_send_keeps_its_place_at_the_front_of_its_conversation()
    {
        using var db = CreateContext();
        var inbox = new SqlInboxStore(db);
        var deliveries = new SqlDeliveryStore(db);

        const string conversation = "conversation-retrying";
        var firstAnswer = await RequestDeliveryAsync(inbox, deliveries, "native-first", conversation, SameInstant);
        await RequestDeliveryAsync(inbox, deliveries, "native-second", conversation, SameInstant.AddSeconds(1));

        var first = Assert.Single(await deliveries.ClaimPendingAsync(10, CancellationToken.None));
        await deliveries.SaveAsync(first.MarkAttemptFailed(), CancellationToken.None);

        var retried = Assert.Single(await deliveries.ClaimPendingAsync(10, CancellationToken.None));

        Assert.Equal(firstAnswer, retried.DeliveryId);
        Assert.Equal(1, retried.Attempts);
        Assert.Equal(DeliveryStatus.Pending, retried.Status);
    }

    [Fact]
    public async Task Response_parts_waiting_the_same_length_of_time_are_claimed_in_a_stable_order()
    {
        using var db = CreateContext();
        var inbox = new SqlInboxStore(db);
        var deliveries = new SqlDeliveryStore(db);

        for (var i = 1; i <= 4; i++)
        {
            await RequestDeliveryAsync(inbox, deliveries, $"native-tie-{i}", $"conversation-tie-{i}", SameInstant);
        }

        var first = await deliveries.ClaimPendingAsync(2, CancellationToken.None);
        var second = await deliveries.ClaimPendingAsync(2, CancellationToken.None);
        var all = await deliveries.ClaimPendingAsync(4, CancellationToken.None);

        Assert.Equal(first.Select(d => d.DeliveryId), second.Select(d => d.DeliveryId));
        Assert.Equal(first.Select(d => d.DeliveryId), all.Take(2).Select(d => d.DeliveryId));
        Assert.Equal(4, all.Count);
    }

    private static async Task<TurnId> AcceptAsync(
        SqlInboxStore inbox, string nativeMessageId, string conversationId, DateTimeOffset receivedAt)
    {
        var accepted = await inbox.AcceptAsync(
            TestTurns.Text(nativeMessageId, SomeParticipant, conversationId, "list stock", null, receivedAt, null),
            CancellationToken.None);

        return accepted.Turn.TurnId;
    }

    /// <summary>Accepts a Turn and records the one channel-neutral response part answering it would leave behind.</summary>
    private static async Task<Guid> RequestDeliveryAsync(
        SqlInboxStore inbox,
        SqlDeliveryStore deliveries,
        string nativeMessageId,
        string conversationId,
        DateTimeOffset receivedAt)
    {
        var turnId = await AcceptAsync(inbox, nativeMessageId, conversationId, receivedAt);
        var delivery = Delivery.Request(turnId, "conversation", "answered", receivedAt);
        await deliveries.SaveAsync(delivery, CancellationToken.None);

        return delivery.DeliveryId;
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
