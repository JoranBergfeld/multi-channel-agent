using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiChannelAgent.Application.Channels.Email;

namespace MultiChannelAgent.Infrastructure.Channels.Email;

/// <summary>Microsoft Graph mailbox adapter for the dedicated inbound email channel mailbox.</summary>
public sealed class GraphMailboxClient(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<EmailChannelOptions> options,
    ILogger<GraphMailboxClient> logger) : IGraphMailboxClient
{
    private static readonly string[] GraphDefaultScope = ["https://graph.microsoft.com/.default"];
    private const string ImmutableIdPreference = "IdType=\"ImmutableId\"";
    private const string TextBodyPreference = "outlook.body-content-type=\"text\"";

    public async Task<GraphInboundMessage?> GetMessageAsync(string messageId, CancellationToken cancellationToken)
    {
        var mailboxAddress = RequireMailboxAddress();
        var escapedMailbox = Uri.EscapeDataString(mailboxAddress);
        var escapedMessageId = Uri.EscapeDataString(messageId);
        var path =
            $"users/{escapedMailbox}/messages/{escapedMessageId}?$select=id,uniqueBody,from,sender,conversationId,receivedDateTime";

        using var response = await SendAsync(
            HttpMethod.Get,
            path,
            request =>
            {
                request.Headers.TryAddWithoutValidation("Prefer", ImmutableIdPreference);
                request.Headers.TryAddWithoutValidation("Prefer", TextBodyPreference);
            },
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureAuthoritative(response);

        var message = await response.Content.ReadFromJsonAsync<GraphMessageDto>(cancellationToken);
        if (message is null
            || string.IsNullOrWhiteSpace(message.Id)
            || string.IsNullOrWhiteSpace(message.ConversationId)
            || message.ReceivedDateTime is null
            || string.IsNullOrWhiteSpace(message.UniqueBody?.Content))
        {
            throw new InvalidOperationException("Microsoft Graph returned an incomplete message payload.");
        }

        var senderAddress = message.Sender?.EmailAddress?.Address ?? message.From?.EmailAddress?.Address;
        if (string.IsNullOrWhiteSpace(senderAddress))
        {
            throw new InvalidOperationException("Microsoft Graph returned a message without a sender address.");
        }

        return new GraphInboundMessage(
            message.Id.Trim(),
            message.ConversationId.Trim(),
            senderAddress.Trim(),
            message.UniqueBody.Content,
            QuotedText: null,
            message.ReceivedDateTime.Value);
    }

    public async Task SendReplyAsync(string messageId, string replyText, CancellationToken cancellationToken)
    {
        var mailboxAddress = RequireMailboxAddress();
        var escapedMailbox = Uri.EscapeDataString(mailboxAddress);
        var escapedMessageId = Uri.EscapeDataString(messageId);

        using var response = await SendAsync(
            HttpMethod.Post,
            $"users/{escapedMailbox}/messages/{escapedMessageId}/reply",
            request => request.Content = JsonContent.Create(new ReplyRequest(replyText)),
            cancellationToken);

        EnsureAuthoritative(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        Action<HttpRequestMessage>? configureRequest,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext(GraphDefaultScope), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to acquire a Microsoft Graph access token.");
            throw new InvalidOperationException("Failed to acquire a Microsoft Graph access token.", ex);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        configureRequest?.Invoke(request);

        try
        {
            return await httpClient.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Microsoft Graph request failed (network/transport error).");
            throw new InvalidOperationException("Microsoft Graph request failed (network/transport error).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Microsoft Graph request timed out.");
            throw new InvalidOperationException("Microsoft Graph request timed out.", ex);
        }
    }

    private void EnsureAuthoritative(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Unlike the tenant-member directory seam, this mailbox seam has no caller that branches on
        // "Graph outage" versus other application-owned failures: inbound processing logs and
        // continues per notification entry, and outbound delivery converts any exception into a
        // retryable false. A typed outage exception would therefore add no distinct behavior here.
        logger.LogWarning("Microsoft Graph request failed with status {StatusCode}.", (int)response.StatusCode);
        throw new InvalidOperationException($"Microsoft Graph request failed with status {(int)response.StatusCode}.");
    }

    private string RequireMailboxAddress()
    {
        var mailboxAddress = options.Value.MailboxAddress;
        if (string.IsNullOrWhiteSpace(mailboxAddress))
        {
            throw new InvalidOperationException("EmailChannel:MailboxAddress must be configured.");
        }

        return mailboxAddress.Trim();
    }

    private sealed record GraphMessageDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("conversationId")] string? ConversationId,
        [property: JsonPropertyName("uniqueBody")] GraphItemBodyDto? UniqueBody,
        [property: JsonPropertyName("sender")] GraphRecipientDto? Sender,
        [property: JsonPropertyName("from")] GraphRecipientDto? From,
        [property: JsonPropertyName("receivedDateTime")] DateTimeOffset? ReceivedDateTime);

    private sealed record GraphItemBodyDto([property: JsonPropertyName("content")] string? Content);

    private sealed record GraphRecipientDto([property: JsonPropertyName("emailAddress")] GraphEmailAddressDto? EmailAddress);

    private sealed record GraphEmailAddressDto([property: JsonPropertyName("address")] string? Address);

    private sealed record ReplyRequest([property: JsonPropertyName("comment")] string Comment);
}
