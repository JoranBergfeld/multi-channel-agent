namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// A normalized, channel-neutral inbound Turn. Every adapter translates validated native input into
/// this shape before it is durably accepted. The <see cref="TurnId"/> is generated once at acceptance;
/// the <see cref="NativeMessageId"/> is the stable native identity used to detect duplicate delivery.
/// </summary>
public sealed record InboundTurn
{
    public required TurnId TurnId { get; init; }

    public required string NativeMessageId { get; init; }

    public required string ChannelConversationId { get; init; }

    public required string ContentText { get; init; }

    public string? Locale { get; init; }

    public string? TraceId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public static InboundTurn Create(
        string? nativeMessageId,
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
            ChannelConversationId = normalizedChannelConversationId,
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
