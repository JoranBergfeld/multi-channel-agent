namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable inbox row for one accepted Turn. <see cref="NativeMessageId"/> carries a unique
/// constraint so duplicate at-least-once delivery cannot create a second row (idempotency at the
/// Turn boundary). <see cref="Status"/> tracks workflow processing state.
/// </summary>
public sealed class InboxEntryEntity
{
    public Guid TurnId { get; set; }

    public required string NativeMessageId { get; set; }

    public required string ChannelConversationId { get; set; }

    public required string ContentText { get; set; }

    public string? Locale { get; set; }

    public string? TraceId { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public InboxEntryStatus Status { get; set; }
}

public enum InboxEntryStatus
{
    Pending = 0,
    Completed = 1,
}
