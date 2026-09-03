using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// A normalized, channel-neutral inbound Turn. Every adapter translates validated native input into
/// this shape before it is durably accepted. The <see cref="TurnId"/> is generated once at
/// acceptance; the <see cref="NativeMessageId"/> is the stable native identity used to detect
/// duplicate delivery. <see cref="ParticipantId"/> and <see cref="ChannelConversationId"/> are the
/// application-owned identities the adapter resolved from trusted context (authenticated claims and
/// the channel's own conversation identifier) - never accepted as untrusted caller input.
/// </summary>
public sealed record InboundTurn
{
    public required TurnId TurnId { get; init; }

    public required string NativeMessageId { get; init; }

    public required ParticipantId ParticipantId { get; init; }

    public required ChannelConversationId ChannelConversationId { get; init; }

    public required string ContentText { get; init; }

    public string? Locale { get; init; }

    public string? TraceId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public static InboundTurn Create(
        string? nativeMessageId,
        ParticipantId participantId,
        string? channelConversationId,
        string? contentText,
        string? locale,
        DateTimeOffset receivedAt,
        string? traceId)
    {
        var normalizedNativeMessageId = RequireNonBlank(nativeMessageId, nameof(nativeMessageId));
        var normalizedChannelConversationId = RequireNonBlank(channelConversationId, nameof(channelConversationId));
        var normalizedContentText = RequireNonBlank(contentText, nameof(contentText));

        return new InboundTurn
        {
            TurnId = TurnId.NewId(),
            NativeMessageId = normalizedNativeMessageId,
            ParticipantId = participantId,
            ChannelConversationId = new ChannelConversationId(normalizedChannelConversationId),
            ContentText = normalizedContentText,
            Locale = NormalizeOptional(locale),
            TraceId = NormalizeOptional(traceId),
            ReceivedAt = receivedAt,
        };
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
