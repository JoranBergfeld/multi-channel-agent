using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiChannelAgent.Application.Channels.Email;
using MultiChannelAgent.Infrastructure.Channels.Email;

namespace MultiChannelAgent.IntegrationTests.Channels;

public sealed class GraphMailboxClientTests
{
    private sealed class StubTokenCredential(string token) : TokenCredential
    {
        public int CallCount { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class RecordingLogger : ILogger<GraphMailboxClient>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private static (GraphMailboxClient Client, StubHttpMessageHandler Handler, StubTokenCredential Credential, RecordingLogger Logger) CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string mailboxAddress = "agent@example.com",
        string token = "fake-access-token")
    {
        var handler = new StubHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        var credential = new StubTokenCredential(token);
        var logger = new RecordingLogger();
        var options = Options.Create(new EmailChannelOptions
        {
            MailboxAddress = mailboxAddress,
            ClientState = "unused-client-state",
        });

        return (new GraphMailboxClient(httpClient, credential, options, logger), handler, credential, logger);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task GetMessageAsync_sends_the_expected_select_and_prefer_headers()
    {
        var (client, handler, credential, _) = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """
        {
            "id": "immutable-123",
            "conversationId": "conversation-456",
            "receivedDateTime": "2026-09-06T20:00:00Z",
            "uniqueBody": { "content": "Direct plain text body" },
            "sender": { "emailAddress": { "address": "sender@example.com" } },
            "from": { "emailAddress": { "address": "from@example.com" } }
        }
        """));

        _ = await client.GetMessageAsync("graph-id-123", CancellationToken.None);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/users/agent%40example.com/messages/graph-id-123?$select=id,uniqueBody,from,sender,conversationId,receivedDateTime",
            request.RequestUri!.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "fake-access-token"), request.Headers.Authorization);
        Assert.Contains(request.Headers.GetValues("Prefer"), value => value.Contains("IdType=\"ImmutableId\"", StringComparison.Ordinal));
        Assert.Contains(request.Headers.GetValues("Prefer"), value => value.Contains("outlook.body-content-type=\"text\"", StringComparison.Ordinal));
        Assert.True(credential.CallCount > 0);
    }

    [Fact]
    public async Task GetMessageAsync_maps_a_complete_graph_message()
    {
        var receivedAt = DateTimeOffset.Parse("2026-09-06T20:00:00Z");
        var (client, _, _, _) = CreateClient(_ => JsonResponse(HttpStatusCode.OK, $$"""
        {
            "id": "immutable-123",
            "conversationId": "conversation-456",
            "receivedDateTime": "{{receivedAt:O}}",
            "uniqueBody": { "content": "Direct plain text body" },
            "sender": { "emailAddress": { "address": "sender@example.com" } },
            "from": { "emailAddress": { "address": "from@example.com" } }
        }
        """));

        var message = await client.GetMessageAsync("graph-id-123", CancellationToken.None);

        Assert.NotNull(message);
        Assert.Equal("immutable-123", message!.ImmutableId);
        Assert.Equal("conversation-456", message.ConversationId);
        Assert.Equal("sender@example.com", message.SenderAddress);
        Assert.Equal("Direct plain text body", message.DirectText);
        Assert.Null(message.QuotedText);
        Assert.Equal(receivedAt, message.ReceivedAt);
    }

    [Fact]
    public async Task GetMessageAsync_returns_null_for_404()
    {
        var (client, _, _, _) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var message = await client.GetMessageAsync("missing-id", CancellationToken.None);

        Assert.Null(message);
    }

    [Fact]
    public async Task GetMessageAsync_rejects_an_incomplete_payload()
    {
        var (client, _, _, _) = CreateClient(_ => JsonResponse(HttpStatusCode.OK, """
        {
            "id": "immutable-123",
            "conversationId": "conversation-456",
            "receivedDateTime": "2026-09-06T20:00:00Z",
            "uniqueBody": { "content": " " },
            "sender": { "emailAddress": { "address": "sender@example.com" } }
        }
        """));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetMessageAsync("graph-id-123", CancellationToken.None));

        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMessageAsync_throws_for_non_success_statuses_other_than_404()
    {
        var (client, _, _, logger) = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetMessageAsync("graph-id-123", CancellationToken.None));

        Assert.Contains("503", error.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Messages, message => message.Contains("503", StringComparison.Ordinal));
    }
}
