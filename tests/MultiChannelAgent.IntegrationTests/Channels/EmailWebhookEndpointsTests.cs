using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MultiChannelAgent.Application.Channels.Email;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.IntegrationTests.Channels;

public sealed class EmailWebhookEndpointsTests : IAsyncLifetime
{
    private const string ClientState = "email-client-state-secret";

    private SqliteWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private FakeGraphMailboxClient _graphMailboxClient = null!;
    private TrackingTenantMemberDirectory _tenantDirectory = null!;

    public Task InitializeAsync()
    {
        _graphMailboxClient = new FakeGraphMailboxClient();
        _tenantDirectory = new TrackingTenantMemberDirectory();
        _factory = new SqliteWebApplicationFactory(configureTestServices: services =>
        {
            services.RemoveAll<IOptions<EmailChannelOptions>>();
            services.AddSingleton<IOptions<EmailChannelOptions>>(Options.Create(new EmailChannelOptions
            {
                MailboxAddress = "agent@example.com",
                ClientState = ClientState,
            }));
            services.RemoveAll<IGraphMailboxClient>();
            services.AddSingleton<IGraphMailboxClient>(_graphMailboxClient);
            services.RemoveAll<ITenantMemberDirectory>();
            services.AddSingleton<ITenantMemberDirectory>(_tenantDirectory);
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Validation_handshake_echoes_the_token_verbatim_as_plain_text()
    {
        var response = await _client.PostAsync("/api/channels/email/notifications?validationToken=hello%20graph", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("hello graph", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_client_state_returns_202_and_creates_no_turn()
    {
        _graphMailboxClient.Messages["graph-message-1"] = new GraphInboundMessage(
            ImmutableId: "immutable-1",
            ConversationId: "conversation-1",
            SenderAddress: "sender@example.com",
            DirectText: "ignored",
            QuotedText: null,
            ReceivedAt: DateTimeOffset.Parse("2026-09-06T19:00:00+00:00"));

        var response = await _client.PostAsJsonAsync("/api/channels/email/notifications", new
        {
            value = new[]
            {
                new
                {
                    clientState = "wrong-secret",
                    resourceData = new { id = "graph-message-1" },
                },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var inboxStore = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        var turn = await inboxStore.FindByNativeMessageIdAsync(
            new NativeMessageKey(
                new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                new ChannelConversationId("conversation-1"),
                "immutable-1"),
            CancellationToken.None);

        Assert.Null(turn);
    }

    [Fact]
    public async Task Valid_notification_returns_202_and_durably_accepts_a_turn()
    {
        var participantId = new ParticipantId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        _graphMailboxClient.Messages["graph-message-2"] = new GraphInboundMessage(
            ImmutableId: "immutable-2",
            ConversationId: "conversation-2",
            SenderAddress: "sender@example.com",
            DirectText: "count the blue gloves",
            QuotedText: "older context",
            ReceivedAt: DateTimeOffset.Parse("2026-09-06T20:00:00+00:00"));
        _tenantDirectory.Register("sender@example.com", new ResolvedTenantMember(participantId, "Sender"));

        var response = await _client.PostAsJsonAsync("/api/channels/email/notifications", new
        {
            value = new[]
            {
                new
                {
                    clientState = ClientState,
                    resourceData = new { id = "graph-message-2" },
                },
            },
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(["graph-message-2"], _graphMailboxClient.RequestedMessageIds);
        Assert.Equal(["sender@example.com"], _tenantDirectory.RequestedAddresses);

        using var verifyScope = _factory.Services.CreateScope();
        var inboxStore = verifyScope.ServiceProvider.GetRequiredService<IInboxStore>();
        var pendingTurns = await inboxStore.ClaimPendingAsync(10, CancellationToken.None);
        var turn = pendingTurns.SingleOrDefault(t => t.Channel == EmailChannel.Name);

        Assert.NotNull(turn);
        Assert.Equal(participantId, turn!.ParticipantId);
        Assert.Equal("immutable-2", turn.NativeMessageId);
        Assert.Equal(new ChannelConversationId("conversation-2"), turn.ChannelConversationId);
        Assert.Equal(EmailChannel.Name, turn!.Channel);
        Assert.Equal(ChannelPrincipalKind.ExchangeMailbox, turn.Principal.Kind);
        Assert.Equal("sender@example.com", turn.Principal.Subject);
        Assert.Equal(
            [ContentProvenance.Direct, ContentProvenance.Quoted],
            turn.ContentParts.Select(part => part.Provenance));
        Assert.Equal(
            ["count the blue gloves", "older context"],
            turn.ContentParts.Select(part => part.Text));
    }

    private sealed class FakeGraphMailboxClient : IGraphMailboxClient
    {
        public Dictionary<string, GraphInboundMessage> Messages { get; } = new(StringComparer.Ordinal);

        public List<string> RequestedMessageIds { get; } = [];

        public List<(string MessageId, string ReplyText)> Replies { get; } = [];

        public Task<GraphInboundMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken)
        {
            RequestedMessageIds.Add(messageId);
            return Task.FromResult(Messages.GetValueOrDefault(messageId));
        }

        public Task SendReplyAsync(string messageId, string replyText, CancellationToken cancellationToken)
        {
            Replies.Add((messageId, replyText));
            return Task.CompletedTask;
        }
    }

    private sealed class TrackingTenantMemberDirectory : ITenantMemberDirectory
    {
        private readonly Dictionary<string, ResolvedTenantMember> _byAddress = new(StringComparer.OrdinalIgnoreCase);

        public List<string> RequestedAddresses { get; } = [];

        public void Register(string address, ResolvedTenantMember member) => _byAddress[address] = member;

        public Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken)
        {
            if (identifier.Address is { } address)
            {
                RequestedAddresses.Add(address);
                return Task.FromResult(_byAddress.GetValueOrDefault(address));
            }

            return Task.FromResult<ResolvedTenantMember?>(null);
        }
    }
}
