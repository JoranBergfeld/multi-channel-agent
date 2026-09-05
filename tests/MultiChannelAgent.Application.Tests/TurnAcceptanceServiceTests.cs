using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class TurnAcceptanceServiceTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly DateTimeOffset ReceivedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string SomeConversation = "conversation-1";

    /// <summary>The ordinary signed-in web submission; only what a test is about ever varies.</summary>
    private static SubmitTurnRequest SubmitRequest(
        string nativeMessageId,
        ParticipantId participantId,
        string channelConversationId,
        string contentText,
        string? locale,
        string? traceId) => new(
            nativeMessageId,
            participantId,
            channelConversationId,
            "web",
            ChannelPrincipal.EntraUser(participantId.Value.ToString(), "22222222-2222-2222-2222-222222222222"),
            ChannelCapabilities.Text | ChannelCapabilities.ProgressEvents,
            contentText,
            locale,
            traceId);

    [Fact]
    public async Task Accepting_a_new_turn_durably_stores_it_and_returns_a_fresh_turn_id()
    {
        var store = new InMemoryInboxStore();
        var service = new TurnAcceptanceService(store, new InMemoryFoundryConversationBindingStore());

        var result = await service.AcceptAsync(
            SubmitRequest("native-1", SomeParticipant, "conversation-1", "hello", "en-US", "trace-1"),
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
        var service = new TurnAcceptanceService(store, new InMemoryFoundryConversationBindingStore());
        var request = SubmitRequest("native-1", SomeParticipant, "conversation-1", "hello", "en-US", "trace-1");

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
        var request = SubmitRequest("native-race-1", SomeParticipant, "conversation-race-1", "hello", "en-US", "trace-1");

        // Both parties are gated so that each only proceeds into AcceptAsync once BOTH have already
        // called FindByNativeMessageIdAsync and observed nothing - the exact "two simultaneous
        // deliveries both observe absence" race the durable-acceptance contract must survive, forced
        // deterministically instead of left to thread-scheduling luck.
        var readyA = new TaskCompletionSource();
        var readyB = new TaskCompletionSource();

        // One binding store, shared exactly as the inbox is: two racing deliveries of the same native
        // message resolve the very same Foundry conversation, so what the loser discards is only its
        // own duplicate Turn - never a competing binding for the winner's.
        var bindings = new InMemoryFoundryConversationBindingStore();
        var serviceA = new TurnAcceptanceService(new TwoPartyGatedInboxStore(store, readyA, readyB.Task), bindings);
        var serviceB = new TurnAcceptanceService(new TwoPartyGatedInboxStore(store, readyB, readyA.Task), bindings);

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
        var service = new TurnAcceptanceService(store, new InMemoryFoundryConversationBindingStore());

        var mine = await service.AcceptAsync(
            SubmitRequest("native-shared", SomeParticipant, "conversation-1", "hello", null, null), ReceivedAt, CancellationToken.None);
        var theirs = await service.AcceptAsync(
            SubmitRequest("native-shared", otherParticipant, "conversation-1", "hello", null, null), ReceivedAt, CancellationToken.None);

        Assert.NotEqual(mine.TurnId, theirs.TurnId);
        Assert.False(theirs.WasAlreadyAccepted);
        Assert.Equal(2, store.Turns.Count);
    }

    [Fact]
    public async Task The_same_native_message_id_in_a_different_conversation_is_accepted_as_its_own_turn()
    {
        var store = new InMemoryInboxStore();
        var service = new TurnAcceptanceService(store, new InMemoryFoundryConversationBindingStore());

        var first = await service.AcceptAsync(
            SubmitRequest("native-shared", SomeParticipant, "conversation-1", "hello", null, null), ReceivedAt, CancellationToken.None);
        var second = await service.AcceptAsync(
            SubmitRequest("native-shared", SomeParticipant, "conversation-2", "hello", null, null), ReceivedAt, CancellationToken.None);

        Assert.NotEqual(first.TurnId, second.TurnId);
        Assert.False(second.WasAlreadyAccepted);
        Assert.Equal(2, store.Turns.Count);
    }

    [Fact]
    public async Task An_accepted_turn_captures_the_foundry_conversation_it_was_accepted_under()
    {
        var inbox = new InMemoryInboxStore();
        var bindings = new InMemoryFoundryConversationBindingStore();
        var service = new TurnAcceptanceService(inbox, bindings);

        var accepted = await service.AcceptAsync(
            SubmitRequest("native-capture-1", SomeParticipant, SomeConversation, "hello", null, null),
            ReceivedAt,
            CancellationToken.None);

        var binding = Assert.Single(bindings.Bindings);
        var captured = await inbox.FindCapturedBindingAsync(accepted.TurnId, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(binding.FoundryConversationId, captured.FoundryConversationId);
        Assert.Equal(binding.Generation, captured.Generation);
    }

    [Fact]
    public async Task Work_accepted_before_a_reset_keeps_the_conversation_it_was_accepted_under()
    {
        var inbox = new InMemoryInboxStore();
        var bindings = new InMemoryFoundryConversationBindingStore();
        var service = new TurnAcceptanceService(inbox, bindings);

        var before = await service.AcceptAsync(
            SubmitRequest("native-capture-2", SomeParticipant, SomeConversation, "hello", null, null),
            ReceivedAt,
            CancellationToken.None);
        var rotated = bindings.Rotate(SomeParticipant, new ChannelConversationId(SomeConversation), ReceivedAt.AddMinutes(1));
        var after = await service.AcceptAsync(
            SubmitRequest("native-capture-3", SomeParticipant, SomeConversation, "hello", null, null),
            ReceivedAt.AddMinutes(2),
            CancellationToken.None);

        var capturedBefore = await inbox.FindCapturedBindingAsync(before.TurnId, CancellationToken.None);
        var capturedAfter = await inbox.FindCapturedBindingAsync(after.TurnId, CancellationToken.None);

        Assert.NotNull(capturedBefore);
        Assert.NotNull(capturedAfter);

        // This is the whole reason the binding is captured rather than resolved at processing time:
        // work accepted before a reset can never end up in the history the reset created.
        Assert.NotEqual(capturedBefore.FoundryConversationId, capturedAfter.FoundryConversationId);
        Assert.Equal(rotated.FoundryConversationId, capturedAfter.FoundryConversationId);
        Assert.Equal(rotated.Generation, capturedAfter.Generation);
    }
}
