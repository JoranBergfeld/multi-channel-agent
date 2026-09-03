namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>The durable, terminal semantic Outcome row for one Turn. Written exactly once per Turn.</summary>
public sealed class OutcomeEntity
{
    public Guid TurnId { get; set; }

    public OutcomeEntityStatus Status { get; set; }

    public required string Code { get; set; }

    public required string Summary { get; set; }

    public string? Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public enum OutcomeEntityStatus
{
    Completed = 0,
    Failed = 1,
}
