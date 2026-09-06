using System.Security.Claims;
using System.Text.Json;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Channels.Teams;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Host.Channels.Teams;

namespace MultiChannelAgent.IntegrationTests.Channels;

public sealed class InventoryAgentTeamsHandlerTests
{
    [Fact]
    public async Task Personal_chat_message_with_a_valid_aad_object_id_accepts_a_turn_and_saves_a_conversation_reference()
    {
        var now = new DateTimeOffset(2026, 9, 6, 19, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);
        var referenceStore = new CapturingTeamsConversationReferenceStore();
        var participantStore = new CapturingParticipantStore();
        var bindingStore = new StubFoundryConversationBindingStore();
        var inboxStore = new CapturingInboxStore();
        var handler = new InventoryAgentTeamsHandler(referenceStore, participantStore, bindingStore, inboxStore, timeProvider);
        var activity = CreateMessageActivity(
            aadObjectId: "11111111-1111-1111-1111-111111111111",
            conversationType: ConversationTypes.Personal,
            conversationId: "teams-conversation-1",
            text: "show my inventory",
            tenantId: "22222222-2222-2222-2222-222222222222");

        await handler.OnTurnAsync(new TurnContext(new StubChannelAdapter(), activity, new ClaimsIdentity()), CancellationToken.None);

        Assert.Equal("teams-conversation-1", referenceStore.SavedConversationId);
        var savedReference = JsonSerializer.Deserialize<ConversationReference>(referenceStore.SavedSerializedReference);
        Assert.NotNull(savedReference);
        Assert.Equal("teams-conversation-1", savedReference!.Conversation.Id);
        Assert.Equal("11111111-1111-1111-1111-111111111111", savedReference.User.AadObjectId);

        var participant = Assert.Single(participantStore.UpsertedParticipants);
        Assert.Equal(new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")), participant.Id);
        Assert.Equal("Ada Lovelace", participant.DisplayName);

        Assert.Equal(new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")), bindingStore.ParticipantId);
        Assert.Equal(new ChannelConversationId("teams-conversation-1"), bindingStore.ChannelConversationId);
        Assert.Equal(now, bindingStore.ReceivedAt);

        var accepted = Assert.Single(inboxStore.AcceptedTurns);
        Assert.Equal("activity-1", accepted.Turn.NativeMessageId);
        Assert.Equal(TeamsChannel.Name, accepted.Turn.Channel);
        Assert.Equal(new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")), accepted.Turn.ParticipantId);
        Assert.Equal(new ChannelConversationId("teams-conversation-1"), accepted.Turn.ChannelConversationId);
        Assert.Equal(ChannelPrincipalKind.EntraUser, accepted.Turn.Principal.Kind);
        Assert.Equal("11111111-1111-1111-1111-111111111111", accepted.Turn.Principal.Subject);
        Assert.Equal("22222222-2222-2222-2222-222222222222", accepted.Turn.Principal.TenantId);
        Assert.Equal(ChannelCapabilities.Text, accepted.Turn.Capabilities);
        Assert.Equal(InputModality.Text, accepted.Turn.InputModality);
        var content = Assert.Single(accepted.Turn.ContentParts);
        Assert.Equal(ContentProvenance.Direct, content.Provenance);
        Assert.Equal("show my inventory", content.Text);
        Assert.Equal(now, accepted.Turn.ReceivedAt);
    }

    [Fact]
    public async Task Group_chat_messages_are_ignored_without_saving_or_accepting_anything()
    {
        var referenceStore = new CapturingTeamsConversationReferenceStore();
        var participantStore = new CapturingParticipantStore();
        var bindingStore = new StubFoundryConversationBindingStore();
        var inboxStore = new CapturingInboxStore();
        var handler = new InventoryAgentTeamsHandler(referenceStore, participantStore, bindingStore, inboxStore, new FakeTimeProvider());
        var activity = CreateMessageActivity(
            aadObjectId: "11111111-1111-1111-1111-111111111111",
            conversationType: "groupChat",
            conversationId: "teams-conversation-2",
            text: "ignore me",
            tenantId: "22222222-2222-2222-2222-222222222222");

        await handler.OnTurnAsync(new TurnContext(new StubChannelAdapter(), activity, new ClaimsIdentity()), CancellationToken.None);

        Assert.Null(referenceStore.SavedConversationId);
        Assert.Empty(participantStore.UpsertedParticipants);
        Assert.Null(bindingStore.ParticipantId);
        Assert.Empty(inboxStore.AcceptedTurns);
    }

    [Fact]
    public async Task Messages_without_a_valid_aad_object_id_are_ignored_without_saving_or_accepting_anything()
    {
        var referenceStore = new CapturingTeamsConversationReferenceStore();
        var participantStore = new CapturingParticipantStore();
        var bindingStore = new StubFoundryConversationBindingStore();
        var inboxStore = new CapturingInboxStore();
        var handler = new InventoryAgentTeamsHandler(referenceStore, participantStore, bindingStore, inboxStore, new FakeTimeProvider());
        var activity = CreateMessageActivity(
            aadObjectId: "not-a-guid",
            conversationType: ConversationTypes.Personal,
            conversationId: "teams-conversation-3",
            text: "ignore me",
            tenantId: "22222222-2222-2222-2222-222222222222");

        await handler.OnTurnAsync(new TurnContext(new StubChannelAdapter(), activity, new ClaimsIdentity()), CancellationToken.None);

        Assert.Null(referenceStore.SavedConversationId);
        Assert.Empty(participantStore.UpsertedParticipants);
        Assert.Null(bindingStore.ParticipantId);
        Assert.Empty(inboxStore.AcceptedTurns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Messages_with_a_missing_or_blank_aad_object_id_are_ignored_without_saving_or_accepting_anything(
        string? aadObjectId)
    {
        var referenceStore = new CapturingTeamsConversationReferenceStore();
        var participantStore = new CapturingParticipantStore();
        var bindingStore = new StubFoundryConversationBindingStore();
        var inboxStore = new CapturingInboxStore();
        var handler = new InventoryAgentTeamsHandler(referenceStore, participantStore, bindingStore, inboxStore, new FakeTimeProvider());
        var activity = CreateMessageActivity(
            aadObjectId: aadObjectId,
            conversationType: ConversationTypes.Personal,
            conversationId: "teams-conversation-4",
            text: "ignore me",
            tenantId: "22222222-2222-2222-2222-222222222222");

        await handler.OnTurnAsync(new TurnContext(new StubChannelAdapter(), activity, new ClaimsIdentity()), CancellationToken.None);

        Assert.Null(referenceStore.SavedConversationId);
        Assert.Empty(participantStore.UpsertedParticipants);
        Assert.Null(bindingStore.ParticipantId);
        Assert.Empty(inboxStore.AcceptedTurns);
    }

    private static Activity CreateMessageActivity(
        string? aadObjectId,
        string conversationType,
        string conversationId,
        string text,
        string tenantId) => new()
        {
            Type = ActivityTypes.Message,
            Id = "activity-1",
            ChannelId = "msteams",
            ServiceUrl = "https://service.example.test",
            Text = text,
            From = new ChannelAccount
            {
                AadObjectId = aadObjectId,
                Id = "user-1",
                Name = "Ada Lovelace",
            },
            Recipient = new ChannelAccount
            {
                Id = "bot-1",
                Name = "Inventory Agent",
            },
            Conversation = new ConversationAccount
            {
                Id = conversationId,
                ConversationType = conversationType,
                TenantId = tenantId,
            },
        };

    private sealed class CapturingTeamsConversationReferenceStore : ITeamsConversationReferenceStore
    {
        public string? SavedConversationId { get; private set; }

        public string SavedSerializedReference { get; private set; } = string.Empty;

        public Task SaveAsync(string channelConversationId, string serializedReference, CancellationToken cancellationToken)
        {
            SavedConversationId = channelConversationId;
            SavedSerializedReference = serializedReference;
            return Task.CompletedTask;
        }

        public Task<string?> FindAsync(string channelConversationId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingParticipantStore : IParticipantStore
    {
        public List<Participant> UpsertedParticipants { get; } = [];

        public Task UpsertAsync(Participant participant, CancellationToken cancellationToken)
        {
            UpsertedParticipants.Add(participant);
            return Task.CompletedTask;
        }
    }

    private sealed class StubFoundryConversationBindingStore : IFoundryConversationBindingStore
    {
        public ParticipantId? ParticipantId { get; private set; }

        public ChannelConversationId? ChannelConversationId { get; private set; }

        public DateTimeOffset? ReceivedAt { get; private set; }

        public Task<FoundryConversationBinding> GetOrCreateAsync(
            ParticipantId participantId,
            ChannelConversationId channelConversationId,
            DateTimeOffset createdAt,
            CancellationToken cancellationToken)
        {
            ParticipantId = participantId;
            ChannelConversationId = channelConversationId;
            ReceivedAt = createdAt;
            return Task.FromResult(FoundryConversationBinding.CreateFirstGeneration(participantId, channelConversationId, createdAt));
        }

        public Task<bool> TryRotateAsync(ParticipantId participantId, ChannelConversationId channelConversationId, FoundryConversationId expectedCurrentFoundryConversationId, int expectedCurrentGeneration, DateTimeOffset rotatedAt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<FoundryConversationBinding?> ReadCurrentForSupersessionAsync(
            ParticipantId participantId,
            ChannelConversationId channelConversationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingInboxStore : IInboxStore
    {
        public List<(InboundTurn Turn, FoundryConversationBinding Binding)> AcceptedTurns { get; } = [];

        public Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken) =>
            Task.FromResult<InboundTurn?>(null);

        public Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InboxAcceptResult> AcceptAsync(InboundTurn turn, FoundryConversationBinding binding, CancellationToken cancellationToken)
        {
            AcceptedTurns.Add((turn, binding));
            return Task.FromResult(new InboxAcceptResult(turn, WasAlreadyAccepted: false));
        }

        public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubChannelAdapter : IChannelAdapter
    {
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

        public Task ContinueConversationAsync(ClaimsIdentity claimsIdentity, ConversationReference reference, string audience, AgentCallbackHandler callback, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        public Task<ResourceResponse[]> SendActivitiesAsync(ITurnContext turnContext, IActivity[] activities, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<ResourceResponse>());

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
