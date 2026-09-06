using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Turns;

namespace MultiChannelAgent.IntegrationTests.Turns;

public sealed class ChannelRoutingDeliverySenderTests
{
    private sealed class StubInboxStore(InboundTurn? turn) : IInboxStore
    {
        public Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken) =>
            Task.FromResult(turn);

        public Task<InboxAcceptResult> AcceptAsync(
            InboundTurn turn,
            FoundryConversationBinding binding,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingDeliverySender : IDeliverySender
    {
        public Delivery? LastDelivery { get; private set; }

        public Task<bool> TrySendAsync(Delivery delivery, CancellationToken cancellationToken)
        {
            LastDelivery = delivery;
            return Task.FromResult(true);
        }
    }

    private sealed class CapturingLogger : ILogger<ChannelRoutingDeliverySender>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task A_delivery_routes_to_the_sender_registered_for_the_origin_turns_channel()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "payload", DateTimeOffset.UnixEpoch);
        var originTurn = ValidOriginTurn(delivery.TurnId, "web");
        var inboxStore = new StubInboxStore(originTurn);
        var keyedSender = new CapturingDeliverySender();
        var services = new ServiceCollection()
            .AddKeyedScoped<IDeliverySender>("web", (_, _) => keyedSender)
            .BuildServiceProvider();

        var sent = await new ChannelRoutingDeliverySender(inboxStore, services, new CapturingLogger())
            .TrySendAsync(delivery, CancellationToken.None);

        Assert.True(sent);
        Assert.Same(delivery, keyedSender.LastDelivery);
    }

    [Fact]
    public async Task A_delivery_with_no_matching_origin_turn_returns_false()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "payload", DateTimeOffset.UnixEpoch);
        var logger = new CapturingLogger();
        var services = new ServiceCollection().BuildServiceProvider();

        var sent = await new ChannelRoutingDeliverySender(new StubInboxStore(null), services, logger)
            .TrySendAsync(delivery, CancellationToken.None);

        Assert.False(sent);
        Assert.Contains(logger.Messages, message => message.Contains(delivery.TurnId.Value.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delivery_whose_origin_turn_has_no_channel_returns_false()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "payload", DateTimeOffset.UnixEpoch);
        var logger = new CapturingLogger();
        var services = new ServiceCollection().BuildServiceProvider();

        var sent = await new ChannelRoutingDeliverySender(
                new StubInboxStore(ValidOriginTurn(delivery.TurnId, "web") with { Channel = null! }),
                services,
                logger)
            .TrySendAsync(delivery, CancellationToken.None);

        Assert.False(sent);
        Assert.Contains(logger.Messages, message => message.Contains(delivery.TurnId.Value.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delivery_for_an_unregistered_origin_channel_returns_false()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "payload", DateTimeOffset.UnixEpoch);
        var logger = new CapturingLogger();
        var services = new ServiceCollection().BuildServiceProvider();

        var sent = await new ChannelRoutingDeliverySender(
                new StubInboxStore(ValidOriginTurn(delivery.TurnId, "email")),
                services,
                logger)
            .TrySendAsync(delivery, CancellationToken.None);

        Assert.False(sent);
        Assert.Contains(logger.Messages, message => message.Contains("email", StringComparison.Ordinal));
    }

    private static InboundTurn ValidOriginTurn(TurnId turnId, string channel) =>
        TestTurns.Text(
            nativeMessageId: "native-123",
            participantId: new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            channelConversationId: "conversation-123",
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UnixEpoch,
            traceId: null) with
        {
            TurnId = turnId,
            Channel = channel,
        };
}
