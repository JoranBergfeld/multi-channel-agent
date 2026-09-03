namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// Strongly typed native-channel conversation identity carried by every InboundTurn. Wraps the
/// channel adapter's own opaque conversation identifier (for the web channel, the browser-profile
/// conversation cookie) so it is never confused with a <see cref="FoundryConversationId"/> or any
/// other identity in this domain.
/// </summary>
public readonly record struct ChannelConversationId(string Value)
{
    public override string ToString() => Value;
}
