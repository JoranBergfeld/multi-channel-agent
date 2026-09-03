using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests;

public class TurnAcceptanceServiceTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly DateTimeOffset ReceivedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accepting_a_new_turn_durably_stores_it_and_returns_a_fresh_turn_id()
    {
        var store = new InMemoryInboxStore();
        var service = new TurnAcceptanceService(store);

        var result = await service.AcceptAsync(
            new SubmitTurnRequest("native-1", SomeParticipant, "conversation-1", "hello", "en-US", "trace-1"),
            ReceivedAt,
            CancellationToken.None);

        Assert.False(result.WasAlreadyAccepted);
        Assert.Single(store.Turns);
        Assert.Equal(result.TurnId, store.Turns[0].TurnId);
        Assert.Equal("native-1", store.Turns[0].NativeMessageId);
    }

    [Fact]
    public async Task Accepting_the_same_native_message_twice_returns_the_first_turn_id_without_duplicating()
    {
        var store = new InMemoryInboxStore();
        var service = new TurnAcceptanceService(store);
        var request = new SubmitTurnRequest("native-1", SomeParticipant, "conversation-1", "hello", "en-US", "trace-1");

        var first = await service.AcceptAsync(request, ReceivedAt, CancellationToken.None);
        var second = await service.AcceptAsync(request, ReceivedAt.AddMinutes(1), CancellationToken.None);

        Assert.Equal(first.TurnId, second.TurnId);
        Assert.True(second.WasAlreadyAccepted);
        Assert.Single(store.Turns);
    }

    [Fact]
    public async Task Two_concurrent_deliveries_of_the_same_native_message_id_both_observing_absence_converge_on_one_turn()
    {
        var store = new InMemoryInboxStore();
        var request = new SubmitTurnRequest("native-race-1", SomeParticipant, "conversation-race-1", "hello", "en-US", "trace-1");

        // Both parties are gated so that each only proceeds into AcceptAsync once BOTH have already
        // called FindByNativeMessageIdAsync and observed nothing - the exact "two simultaneous
        // deliveries both observe absence" race the durable-acceptance contract must survive, forced
        // deterministically instead of left to thread-scheduling luck.
        var readyA = new TaskCompletionSource();
        var readyB = new TaskCompletionSource();
        var serviceA = new TurnAcceptanceService(new TwoPartyGatedInboxStore(store, readyA, readyB.Task));
        var serviceB = new TurnAcceptanceService(new TwoPartyGatedInboxStore(store, readyB, readyA.Task));

        var taskA = serviceA.AcceptAsync(request, ReceivedAt, CancellationToken.None);
        var taskB = serviceB.AcceptAsync(request, ReceivedAt, CancellationToken.None);

        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(results[0].TurnId, results[1].TurnId);
        Assert.Single(results, r => !r.WasAlreadyAccepted);
        Assert.Single(results, r => r.WasAlreadyAccepted);
        Assert.Single(store.Turns);
    }

    // A native message id is only unique within the scope that issued it. Two Participants (or two
    // conversations of one Participant) that happen to mint the same opaque id are unrelated
    // messages, and collapsing them into one Turn would silently drop a real request - and, worse,
    // hand one Participant another Participant's recorded Outcome.
    [Fact]
    public async Task The_same_native_message_id_from_a_different_participant_is_accepted_as_its_own_turn()
    {
        var otherParticipant = new ParticipantId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        var store = new InMemoryInboxStore();
        var service = new TurnAcceptanceService(store);

        var mine = await service.AcceptAsync(
            new SubmitTurnRequest("native-shared", SomeParticipant, "conversation-1", "hello", null, null), ReceivedAt, CancellationToken.None);
        var theirs = await service.AcceptAsync(
            new SubmitTurnRequest("native-shared", otherParticipant, "conversation-1", "hello", null, null), ReceivedAt, CancellationToken.None);

        Assert.NotEqual(mine.TurnId, theirs.TurnId);
        Assert.False(theirs.WasAlreadyAccepted);
        Assert.Equal(2, store.Turns.Count);
    }

    [Fact]
    public async Task The_same_native_message_id_in_a_different_conversation_is_accepted_as_its_own_turn()
    {
        var store = new InMemoryInboxStore();
        var service = new TurnAcceptanceService(store);

        var first = await service.AcceptAsync(
            new SubmitTurnRequest("native-shared", SomeParticipant, "conversation-1", "hello", null, null), ReceivedAt, CancellationToken.None);
        var second = await service.AcceptAsync(
            new SubmitTurnRequest("native-shared", SomeParticipant, "conversation-2", "hello", null, null), ReceivedAt, CancellationToken.None);

        Assert.NotEqual(first.TurnId, second.TurnId);
        Assert.False(second.WasAlreadyAccepted);
        Assert.Equal(2, store.Turns.Count);
    }
}
