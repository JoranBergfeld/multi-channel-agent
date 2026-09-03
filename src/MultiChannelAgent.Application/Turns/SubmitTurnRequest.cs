namespace MultiChannelAgent.Application.Turns;

/// <summary>A normalized synthetic Turn submitted at the application boundary, before durable acceptance.</summary>
public sealed record SubmitTurnRequest(
    string NativeMessageId,
    string ChannelConversationId,
    string ContentText,
    string? Locale,
    string? TraceId);
