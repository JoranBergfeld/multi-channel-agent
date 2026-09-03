namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>The durable, terminal semantic Outcome row for one Turn. Written exactly once per Turn.</summary>
public sealed class OutcomeEntity
{
    public Guid TurnId { get; set; }

    public OutcomeEntityStatus Status { get; set; }

    /// <summary>
    /// The semantic shape of the answer (<c>completed</c>, <c>not_found</c>, <c>ambiguous</c>, ...),
    /// stored alongside <see cref="Status"/> so a deterministic domain answer is never conflated with
    /// the system failing.
    /// </summary>
    public OutcomeEntityCategory Category { get; set; }

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

public enum OutcomeEntityCategory
{
    Completed = 0,
    ConfirmationRequired = 1,
    Ambiguous = 2,
    NotFound = 3,
    Forbidden = 4,
    Conflict = 5,
    Invalid = 6,
    TransientFailure = 7,
}
