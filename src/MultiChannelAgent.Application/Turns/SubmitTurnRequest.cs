using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Turns;

/// <summary>
/// A normalized Turn submitted at the application boundary by a channel adapter, before durable
/// acceptance. <see cref="ParticipantId"/>, <see cref="ChannelConversationId"/>, and
/// <see cref="Principal"/> are trusted application context the adapter (an authenticated HTTP
/// endpoint, or another channel) resolved itself - never accepted as untrusted request-body fields.
/// <see cref="Channel"/> and <see cref="Capabilities"/> declare which channel this arrived on and what
/// it can render, so the core never has to know one channel from another.
/// </summary>
public sealed record SubmitTurnRequest(
    string NativeMessageId,
    ParticipantId ParticipantId,
    string ChannelConversationId,
    string Channel,
    ChannelPrincipal Principal,
    ChannelCapabilities Capabilities,
    string ContentText,
    string? Locale,
    string? TraceId,
    bool WasInterrupted = false);
