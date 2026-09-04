namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

public sealed class TurnProgressEventEntity
{
    public Guid TurnId { get; set; }

    public long Sequence { get; set; }

    public required string Kind { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public long ExpiresAtTicks { get; set; }
}
