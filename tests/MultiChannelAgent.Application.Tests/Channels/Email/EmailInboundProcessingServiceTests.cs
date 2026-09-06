using MultiChannelAgent.Application.Channels.Email;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Channels.Email;

public sealed class EmailInboundProcessingServiceTests
{
    private static readonly ParticipantId ParticipantId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public async Task A_message_with_direct_and_quoted_content_is_accepted_with_preserved_provenance()
    {
        var graphClient = new StubGraphMailboxClient(Message(
            immutableId: "immutable-1",
            conversationId: "conversation-1",
            senderAddress: "sender@example.com",
            directText: "please count gloves",
            quotedText: "previous thread"));
        var directory = new InMemoryTenantMemberDirectory();
        directory.Register("sender@example.com", new ResolvedTenantMember(ParticipantId, "Sender"));
        var inboxStore = new InMemoryInboxStore();
        var participantStore = new InMemoryParticipantStore();

        var service = CreateService(graphClient, directory, participantStore, inboxStore);

        var outcome = await service.ProcessAsync("graph-message-1", CancellationToken.None);

        var accepted = Assert.IsType<EmailProcessingOutcome.Accepted>(outcome);
        Assert.False(accepted.Result.WasAlreadyAccepted);

        var turn = Assert.Single(inboxStore.Turns);
        Assert.Equal(EmailChannel.Name, turn.Channel);
        Assert.Equal("immutable-1", turn.NativeMessageId);
        Assert.Equal(new ChannelConversationId("conversation-1"), turn.ChannelConversationId);
        Assert.Equal(ParticipantId, turn.ParticipantId);
        Assert.Equal(ChannelPrincipalKind.ExchangeMailbox, turn.Principal.Kind);
        Assert.Equal("sender@example.com", turn.Principal.Subject);
        Assert.Null(turn.Principal.TenantId);
        Assert.Equal(ChannelCapabilities.Text, turn.Capabilities);
        Assert.Equal(InputModality.Text, turn.InputModality);
        Assert.Equal(
            [ContentProvenance.Direct, ContentProvenance.Quoted],
            turn.ContentParts.Select(part => part.Provenance));
        Assert.Equal(
            ["please count gloves", "previous thread"],
            turn.ContentParts.Select(part => part.Text));
        Assert.Equal("Sender", participantStore.Participants[ParticipantId].DisplayName);
    }

    [Fact]
    public async Task A_message_without_quoted_content_is_accepted_as_one_direct_part()
    {
        var graphClient = new StubGraphMailboxClient(Message(
            immutableId: "immutable-2",
            conversationId: "conversation-2",
            senderAddress: "sender@example.com",
            directText: "hello there",
            quotedText: null));
        var directory = new InMemoryTenantMemberDirectory();
        directory.Register("sender@example.com", new ResolvedTenantMember(ParticipantId, "Sender"));
        var inboxStore = new InMemoryInboxStore();
        var participantStore = new InMemoryParticipantStore();

        var service = CreateService(graphClient, directory, participantStore, inboxStore);

        var outcome = await service.ProcessAsync("graph-message-2", CancellationToken.None);

        Assert.IsType<EmailProcessingOutcome.Accepted>(outcome);
        var turn = Assert.Single(inboxStore.Turns);
        var part = Assert.Single(turn.ContentParts);
        Assert.Equal(ContentProvenance.Direct, part.Provenance);
        Assert.Equal("hello there", part.Text);
    }

    [Fact]
    public async Task An_unresolved_sender_returns_sender_unresolved_and_never_accepts_a_turn()
    {
        var graphClient = new StubGraphMailboxClient(Message(
            immutableId: "immutable-3",
            conversationId: "conversation-3",
            senderAddress: "external@example.net",
            directText: "hello",
            quotedText: null));
        var directory = new InMemoryTenantMemberDirectory();
        var participantStore = new InMemoryParticipantStore();
        var inboxStore = new TrackingInboxStore();

        var service = CreateService(graphClient, directory, participantStore, inboxStore);

        var outcome = await service.ProcessAsync("graph-message-3", CancellationToken.None);

        var unresolved = Assert.IsType<EmailProcessingOutcome.SenderUnresolved>(outcome);
        Assert.Equal("external@example.net", unresolved.SenderAddress);
        Assert.False(inboxStore.AcceptCalled);
        Assert.Empty(participantStore.Participants);
    }

    [Fact]
    public async Task A_racing_duplicate_accept_returns_already_accepted_without_error()
    {
        var message = Message(
            immutableId: "immutable-4",
            conversationId: "conversation-4",
            senderAddress: "sender@example.com",
            directText: "hello",
            quotedText: null);
        var directory = new InMemoryTenantMemberDirectory();
        directory.Register("sender@example.com", new ResolvedTenantMember(ParticipantId, "Sender"));
        var bindingStore = new InMemoryFoundryConversationBindingStore();
        var participantStore = new InMemoryParticipantStore();
        var winningTurn = InboundTurn.Create(new InboundTurnDraft
        {
            NativeMessageId = message.ImmutableId,
            ParticipantId = ParticipantId,
            ChannelConversationId = message.ConversationId,
            Channel = EmailChannel.Name,
            Principal = ChannelPrincipal.ExchangeMailbox(message.SenderAddress, null),
            Capabilities = ChannelCapabilities.Text,
            ContentParts = [TurnContentPart.Create(1, ContentProvenance.Direct, message.DirectText)],
            ReceivedAt = message.ReceivedAt,
        });
        var inboxStore = new RacingDuplicateInboxStore(winningTurn);

        var service = CreateService(new StubGraphMailboxClient(message), directory, participantStore, inboxStore, bindingStore);

        var outcome = await service.ProcessAsync("graph-message-4", CancellationToken.None);

        var accepted = Assert.IsType<EmailProcessingOutcome.Accepted>(outcome);
        Assert.True(accepted.Result.WasAlreadyAccepted);
        Assert.Same(winningTurn, accepted.Result.Turn);
        Assert.True(inboxStore.AcceptCalled);
        Assert.Equal("Sender", participantStore.Participants[ParticipantId].DisplayName);
    }

    [Fact]
    public async Task A_missing_graph_message_returns_message_not_found()
    {
        var service = CreateService(
            new StubGraphMailboxClient(message: null),
            new InMemoryTenantMemberDirectory(),
            new InMemoryParticipantStore(),
            new InMemoryInboxStore());

        var outcome = await service.ProcessAsync("missing-id", CancellationToken.None);

        var notFound = Assert.IsType<EmailProcessingOutcome.MessageNotFound>(outcome);
        Assert.Equal("missing-id", notFound.MessageId);
    }

    private static EmailInboundProcessingService CreateService(
        IGraphMailboxClient graphClient,
        ITenantMemberDirectory directory,
        IParticipantStore participantStore,
        IInboxStore inboxStore,
        IFoundryConversationBindingStore? bindingStore = null) =>
        new(
            graphClient,
            new EmailSenderResolutionService(directory),
            participantStore,
            inboxStore,
            bindingStore ?? new InMemoryFoundryConversationBindingStore());

    private static GraphInboundMessage Message(
        string immutableId,
        string conversationId,
        string senderAddress,
        string directText,
        string? quotedText) =>
        new(
            immutableId,
            conversationId,
            senderAddress,
            directText,
            quotedText,
            DateTimeOffset.Parse("2026-09-06T19:00:00+00:00"));

    private sealed class StubGraphMailboxClient(GraphInboundMessage? message) : IGraphMailboxClient
    {
        public Task<GraphInboundMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken) =>
            Task.FromResult(message);

        public Task SendReplyAsync(string messageId, string replyText, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingInboxStore : IInboxStore
    {
        public bool AcceptCalled { get; private set; }

        public Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken) =>
            Task.FromResult<InboundTurn?>(null);

        public Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InboxAcceptResult> AcceptAsync(
            InboundTurn turn,
            FoundryConversationBinding binding,
            CancellationToken cancellationToken)
        {
            AcceptCalled = true;
            return Task.FromResult(new InboxAcceptResult(turn, WasAlreadyAccepted: false));
        }

        public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RacingDuplicateInboxStore(InboundTurn winningTurn) : IInboxStore
    {
        public bool AcceptCalled { get; private set; }

        public Task<InboundTurn?> FindByNativeMessageIdAsync(NativeMessageKey key, CancellationToken cancellationToken) =>
            Task.FromResult<InboundTurn?>(null);

        public Task<InboundTurn?> FindByTurnIdAsync(TurnId turnId, CancellationToken cancellationToken) =>
            Task.FromResult<InboundTurn?>(winningTurn);

        public Task<InboxAcceptResult> AcceptAsync(
            InboundTurn turn,
            FoundryConversationBinding binding,
            CancellationToken cancellationToken)
        {
            AcceptCalled = true;
            return Task.FromResult(new InboxAcceptResult(winningTurn, WasAlreadyAccepted: true));
        }

        public Task<CapturedConversationBinding?> FindCapturedBindingAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InboundTurn>> ClaimPendingAsync(int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
