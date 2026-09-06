using Microsoft.Extensions.Logging.Abstractions;
using MultiChannelAgent.Application.Channels.Email;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Channels.Email;

namespace MultiChannelAgent.IntegrationTests.Channels;

public sealed class GraphEmailDeliverySenderTests
{
    [Fact]
    public async Task Delivery_payload_is_replied_to_the_origin_graph_message()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "reply body", DateTimeOffset.UnixEpoch);
        var originTurn = TestTurns.Text(
            nativeMessageId: "immutable-123",
            participantId: new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            channelConversationId: "conversation-123",
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UnixEpoch,
            traceId: null) with
        {
            TurnId = delivery.TurnId,
            Channel = EmailChannel.Name,
        };
        var graphClient = new CapturingGraphMailboxClient();

        var sent = await new GraphEmailDeliverySender(
            graphClient,
            new StubInboxStore(originTurn),
            NullLogger<GraphEmailDeliverySender>.Instance).TrySendAsync(delivery, CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(("immutable-123", "reply body"), Assert.Single(graphClient.Replies));
    }

    [Fact]
    public async Task Missing_origin_turn_returns_false()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "reply body", DateTimeOffset.UnixEpoch);

        var sent = await new GraphEmailDeliverySender(
            new CapturingGraphMailboxClient(),
            new StubInboxStore(null),
            NullLogger<GraphEmailDeliverySender>.Instance).TrySendAsync(delivery, CancellationToken.None);

        Assert.False(sent);
    }

    [Fact]
    public async Task Graph_failures_return_false_instead_of_throwing()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "reply body", DateTimeOffset.UnixEpoch);
        var originTurn = TestTurns.Text(
            nativeMessageId: "immutable-456",
            participantId: new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            channelConversationId: "conversation-456",
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UnixEpoch,
            traceId: null) with
        {
            TurnId = delivery.TurnId,
            Channel = EmailChannel.Name,
        };

        var sent = await new GraphEmailDeliverySender(
            new ThrowingGraphMailboxClient(),
            new StubInboxStore(originTurn),
            NullLogger<GraphEmailDeliverySender>.Instance).TrySendAsync(delivery, CancellationToken.None);

        Assert.False(sent);
    }

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

    private sealed class CapturingGraphMailboxClient : IGraphMailboxClient
    {
        public List<(string MessageId, string ReplyText)> Replies { get; } = [];

        public Task<GraphInboundMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SendReplyAsync(string messageId, string replyText, CancellationToken cancellationToken)
        {
            Replies.Add((messageId, replyText));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingGraphMailboxClient : IGraphMailboxClient
    {
        public Task<GraphInboundMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SendReplyAsync(string messageId, string replyText, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
