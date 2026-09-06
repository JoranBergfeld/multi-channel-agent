using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiChannelAgent.Application.Channels.Email;

namespace MultiChannelAgent.Host.Endpoints;

/// <summary>Maps the Microsoft Graph webhook receiver for inbound email notifications.</summary>
public static class EmailWebhookEndpoints
{
    public static IEndpointRouteBuilder MapEmailWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/channels/email/notifications", async (
            HttpRequest request,
            EmailInboundProcessingService processingService,
            IOptions<EmailChannelOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            if (request.Query.TryGetValue("validationToken", out var validationToken))
            {
                return Results.Text(validationToken.ToString(), "text/plain", Encoding.UTF8);
            }

            EmailNotificationEnvelope? envelope;
            try
            {
                envelope = await request.ReadFromJsonAsync<EmailNotificationEnvelope>(cancellationToken);
            }
            catch
            {
                return Results.BadRequest();
            }

            if (envelope?.Value is null)
            {
                return Results.BadRequest();
            }

            var logger = loggerFactory.CreateLogger(typeof(EmailWebhookEndpoints));
            foreach (var notification in envelope.Value)
            {
                if (!ClientStateMatches(notification.ClientState, options.Value.ClientState))
                {
                    continue;
                }

                var messageId = notification.ResourceData?.Id;
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    continue;
                }

                try
                {
                    await processingService.ProcessAsync(messageId, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to process email notification for message {MessageId}.", messageId);
                }
            }

            return Results.Accepted();
        }).AllowAnonymous();

        return endpoints;
    }

    private static bool ClientStateMatches(string? actual, string? expected)
    {
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var maxLength = Math.Max(actualBytes.Length, expectedBytes.Length);
        var paddedActual = new byte[maxLength];
        var paddedExpected = new byte[maxLength];

        actualBytes.CopyTo(paddedActual, 0);
        expectedBytes.CopyTo(paddedExpected, 0);

        var contentMatches = CryptographicOperations.FixedTimeEquals(paddedActual, paddedExpected);
        return contentMatches && actualBytes.Length == expectedBytes.Length;
    }

    private sealed record EmailNotificationEnvelope(IReadOnlyList<EmailNotificationEntry>? Value);

    private sealed record EmailNotificationEntry(string? ClientState, EmailNotificationResourceData? ResourceData);

    private sealed record EmailNotificationResourceData(string? Id);
}
