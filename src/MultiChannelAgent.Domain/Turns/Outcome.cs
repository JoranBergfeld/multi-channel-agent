namespace MultiChannelAgent.Domain.Turns;

/// <summary>Terminal semantic status of processing an <see cref="InboundTurn"/>.</summary>
public enum OutcomeStatus
{
    Completed,
    Failed,
}

/// <summary>
/// The recorded, terminal semantic result of processing one Turn. An Outcome is written exactly once
/// per Turn and is what the application boundary exposes to callers; it never leaks SQL or model
/// implementation details.
/// </summary>
public sealed record Outcome
{
    public required TurnId TurnId { get; init; }

    public required OutcomeStatus Status { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static Outcome Completed(TurnId turnId, string code, string summary, DateTimeOffset createdAt) =>
        new()
        {
            TurnId = turnId,
            Status = OutcomeStatus.Completed,
            Code = code,
            Summary = summary,
            CreatedAt = createdAt,
        };

    public static Outcome Failed(TurnId turnId, string code, string summary, DateTimeOffset createdAt) =>
        new()
        {
            TurnId = turnId,
            Status = OutcomeStatus.Failed,
            Code = code,
            Summary = summary,
            CreatedAt = createdAt,
        };
}
