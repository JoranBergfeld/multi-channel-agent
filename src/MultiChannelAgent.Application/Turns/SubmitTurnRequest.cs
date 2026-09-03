using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// A normalized synthetic Turn submitted at the application boundary, before durable acceptance.
/// <see cref="ParticipantId"/> and <see cref="ChannelConversationId"/> are trusted application
/// context the caller (an authenticated HTTP endpoint or another channel adapter) resolved itself -
/// never accepted as untrusted request-body fields.
/// </summary>
public sealed record SubmitTurnRequest(
    string NativeMessageId,
    ParticipantId ParticipantId,
    string ChannelConversationId,
    string ContentText,
    string? Locale,
    string? TraceId);
