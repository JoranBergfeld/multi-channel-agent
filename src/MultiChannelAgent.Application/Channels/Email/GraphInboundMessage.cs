namespace MultiChannelAgent.Application.Channels.Email;

/// <summary>
/// One fetched Graph message, already split into direct and quoted/forwarded content via Graph's
/// own <c>uniqueBody</c> - never heuristically parsed - so provenance stays exact.
/// </summary>
public sealed record GraphInboundMessage(
    string ImmutableId,
    string ConversationId,
    string SenderAddress,
    string DirectText,
    string? QuotedText,
    DateTimeOffset ReceivedAt);
