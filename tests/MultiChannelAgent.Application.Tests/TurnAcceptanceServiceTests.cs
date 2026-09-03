using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.Application.Tests;

public class TurnAcceptanceServiceTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accepting_a_new_turn_durably_stores_it_and_returns_a_fresh_turn_id()
    {
        var store = new InMemoryInboxStore();
        var service = new TurnAcceptanceService(store);

        var result = await service.AcceptAsync(
            new SubmitTurnRequest("native-1", "conversation-1", "hello", "en-US", "trace-1"),
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
        var request = new SubmitTurnRequest("native-1", "conversation-1", "hello", "en-US", "trace-1");

        var first = await service.AcceptAsync(request, ReceivedAt, CancellationToken.None);
        var second = await service.AcceptAsync(request, ReceivedAt.AddMinutes(1), CancellationToken.None);

        Assert.Equal(first.TurnId, second.TurnId);
        Assert.True(second.WasAlreadyAccepted);
        Assert.Single(store.Turns);
    }
}
