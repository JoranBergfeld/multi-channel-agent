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
/// implementation details. <see cref="Payload"/> is an optional versioned, application-owned JSON
/// shape carrying a typed semantic result (for example a Stock List page or Find candidates) beyond
/// the human-readable <see cref="Summary"/>; its absence (the pre-existing echo tracer shape) is
/// exactly as valid as its presence.
/// </summary>
public sealed record Outcome
{
    public required TurnId TurnId { get; init; }

    public required OutcomeStatus Status { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public string? Payload { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static Outcome Completed(TurnId turnId, string code, string summary, DateTimeOffset createdAt, string? payload = null) =>
        new()
        {
            TurnId = turnId,
            Status = OutcomeStatus.Completed,
            Code = code,
            Summary = summary,
            Payload = payload,
            CreatedAt = createdAt,
        };

    public static Outcome Failed(TurnId turnId, string code, string summary, DateTimeOffset createdAt, string? payload = null) =>
        new()
        {
            TurnId = turnId,
            Status = OutcomeStatus.Failed,
            Code = code,
            Summary = summary,
            Payload = payload,
            CreatedAt = createdAt,
        };
}
