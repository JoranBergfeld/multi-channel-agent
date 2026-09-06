using System.Security.Claims;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Authentication.Msal.Model;
using MultiChannelAgent.Application.Channels.Teams;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Channels.Teams;

namespace MultiChannelAgent.IntegrationTests.Channels.Teams;

public sealed class TeamsDeliverySenderTests
{
    [Fact]
    public async Task Stored_reference_present_continues_the_conversation_with_the_delivery_payload_text()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "teams reply", DateTimeOffset.UnixEpoch);
        var originTurn = TestTurns.Text(
            nativeMessageId: "activity-1",
            participantId: new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            channelConversationId: "teams-conversation-1",
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UnixEpoch,
            traceId: null) with
        {
            TurnId = delivery.TurnId,
            Channel = TeamsChannel.Name,
        };
        var reference = new ConversationReference
        {
            ChannelId = "msteams",
            ServiceUrl = "https://service.example.test",
            Conversation = new ConversationAccount { Id = "teams-conversation-1", ConversationType = ConversationTypes.Personal },
            User = new ChannelAccount { Id = "user-1", AadObjectId = "11111111-1111-1111-1111-111111111111" },
            Agent = new ChannelAccount { Id = "bot-1" },
        };
        var adapter = new CapturingChannelAdapter();

        var sent = await new TeamsDeliverySender(
            adapter,
            new StubConnections("agent-app-id"),
            new StubInboxStore(originTurn),
            new StubTeamsConversationReferenceStore(JsonSerializer.Serialize(reference)),
            NullLogger<TeamsDeliverySender>.Instance).TrySendAsync(delivery, CancellationToken.None);

        Assert.True(sent);
        Assert.Equal("agent-app-id", adapter.Audience);
        Assert.Equal("agent-app-id", adapter.IdentityAudience);
        Assert.Equal("teams-conversation-1", adapter.ContinuedReference!.Conversation.Id);
        Assert.Equal("teams reply", adapter.SentText);
    }

    [Fact]
    public async Task Missing_stored_reference_returns_false_without_throwing()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "conversation", "teams reply", DateTimeOffset.UnixEpoch);
        var originTurn = TestTurns.Text(
            nativeMessageId: "activity-2",
            participantId: new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            channelConversationId: "teams-conversation-2",
            contentText: "hello",
            locale: null,
            receivedAt: DateTimeOffset.UnixEpoch,
            traceId: null) with
        {
            TurnId = delivery.TurnId,
            Channel = TeamsChannel.Name,
        };
        var adapter = new CapturingChannelAdapter();

        var sent = await new TeamsDeliverySender(
            adapter,
            new StubConnections("agent-app-id"),
            new StubInboxStore(originTurn),
            new StubTeamsConversationReferenceStore(null),
            NullLogger<TeamsDeliverySender>.Instance).TrySendAsync(delivery, CancellationToken.None);

        Assert.False(sent);
        Assert.Null(adapter.ContinuedReference);
        Assert.Null(adapter.SentText);
    }

    private sealed class StubTeamsConversationReferenceStore(string? serializedReference) : ITeamsConversationReferenceStore
    {
        public Task SaveAsync(string channelConversationId, string serializedReference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> FindAsync(string channelConversationId, CancellationToken cancellationToken) =>
            Task.FromResult(serializedReference);
    }

    private sealed class StubInboxStore(InboundTurn? turn) : IInboxStore
    {
        public Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken) =>
            Task.FromResult(turn);

        public Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubConnections(string clientId) : IConnections
    {
        private readonly IAccessTokenProvider _defaultConnection = new StubAccessTokenProvider(clientId);

        public IAccessTokenProvider GetConnection(string name) => _defaultConnection;

        public IAccessTokenProvider GetDefaultConnection() => _defaultConnection;

        public IAccessTokenProvider GetTokenProvider(ClaimsIdentity claimsIdentity, string serviceUrl) => _defaultConnection;

        public IAccessTokenProvider GetTokenProvider(ClaimsIdentity claimsIdentity, IActivity activity) => _defaultConnection;

        public bool TryGetConnection(string name, out IAccessTokenProvider connection)
        {
            connection = _defaultConnection;
            return true;
        }
    }

    private sealed class StubAccessTokenProvider(string clientId) : IAccessTokenProvider
    {
        public ImmutableConnectionSettings ConnectionSettings { get; } = new(new ConnectionSettings
        {
            ClientId = clientId,
            Authority = "https://login.microsoftonline.com/common",
            TenantId = "common",
            Scopes = ["scope"],
        });

        public Task<string> GetAccessTokenAsync(string resourceUrl, IList<string> scopes, bool forceRefresh) =>
            throw new NotSupportedException();

        public Azure.Core.TokenCredential GetTokenCredential() => throw new NotSupportedException();
    }

    private sealed class CapturingChannelAdapter : IChannelAdapter
    {
        public string? IdentityAudience { get; private set; }

        public string? Audience { get; private set; }

        public ConversationReference? ContinuedReference { get; private set; }

        public string? SentText { get; private set; }

        public Microsoft.Extensions.Logging.ILogger? Logger { get; set; } = NullLogger.Instance;

        public IMiddlewareSet MiddlewareSet { get; } = new StubMiddlewareSet();

        public Func<ITurnContext, Exception, Task> OnTurnError { get; set; } = (_, _) => Task.CompletedTask;

        public Task ContinueConversationAsync(string agentId, ConversationReference reference, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ContinueConversationAsync(ClaimsIdentity claimsIdentity, ConversationReference reference, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ContinueConversationAsync(string agentId, IActivity continuationActivity, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ContinueConversationAsync(ClaimsIdentity claimsIdentity, IActivity continuationActivity, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public async Task ContinueConversationAsync(ClaimsIdentity claimsIdentity, ConversationReference reference, string audience, AgentCallbackHandler callback, CancellationToken cancellationToken)
        {
            IdentityAudience = AgentClaims.GetIncomingAudienceClaim(claimsIdentity);
            Audience = audience;
            ContinuedReference = reference;
            await callback(new TurnContext(this, reference.GetContinuationActivity(), claimsIdentity), cancellationToken);
        }

        public Task ContinueConversationAsync(ClaimsIdentity claimsIdentity, IActivity continuationActivity, string audience, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateConversationAsync(string agentAppId, string channelId, string serviceUrl, string audience, ConversationParameters conversationParameters, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ConversationReference> CreateConversationAsync(ClaimsIdentity identity, string channelId, string serviceUrl, string audience, ConversationParameters parameters, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteActivityAsync(ITurnContext turnContext, ConversationReference reference, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InvokeResponse> ProcessActivityAsync(ClaimsIdentity claimsIdentity, IActivity activity, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ProcessProactiveAsync(ClaimsIdentity claimsIdentity, IActivity continuationActivity, string audience, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ProcessProactiveAsync(ClaimsIdentity claimsIdentity, IActivity continuationActivity, Microsoft.Agents.Builder.IAgent agent, CancellationToken cancellationToken, string audience) =>
            throw new NotSupportedException();

        public Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, IActivity[] activities, CancellationToken cancellationToken)
        {
            SentText = activities.Single().Text;
            return Task.FromResult(new[] { new ResourceResponse("reply-1") });
        }

        public Task<ResourceResponse> UpdateActivityAsync(ITurnContext turnContext, IActivity activity, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IChannelAdapter Use(IMiddleware middleware) => this;
    }

    private sealed class StubMiddlewareSet : IMiddlewareSet
    {
        public Task ReceiveActivityWithStatusAsync(ITurnContext turnContext, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            callback(turnContext, cancellationToken);

        public IMiddlewareSet Use(IMiddleware middleware) => this;
    }
}
