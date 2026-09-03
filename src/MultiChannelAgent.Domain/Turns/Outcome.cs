namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// Whether processing an <see cref="InboundTurn"/> produced an answer at all.
/// <see cref="Failed"/> is reserved for the system, the model, or a dependency failing - never for a
/// deterministic semantic answer such as "nothing matched", "that is ambiguous", or "you may not see
/// that", which are answers the workflow completed successfully and carry their own
/// <see cref="OutcomeCategory"/>.
/// </summary>
public enum OutcomeStatus
{
    Completed,
    Failed,
}

/// <summary>
/// The recorded, terminal semantic result of processing one Turn. An Outcome is written exactly once
/// per Turn and is what the application boundary exposes to callers; it never leaks SQL or model
/// implementation details. <see cref="Category"/> carries the semantic shape of the answer and
/// <see cref="Status"/> follows from it, so an ordinary "not found" can never be mistaken - by a
/// caller, an alert, or a retry policy - for the system failing. <see cref="Payload"/> is an optional
/// versioned, application-owned JSON shape carrying a typed semantic result (for example a Stock List
/// page or Find candidates) beyond the human-readable <see cref="Summary"/>; its absence (the
/// pre-existing echo tracer shape) is exactly as valid as its presence.
/// </summary>
public sealed record Outcome
{
    public required TurnId TurnId { get; init; }

    public required OutcomeStatus Status { get; init; }

    public required OutcomeCategory Category { get; init; }

    public required string Code { get; init; }

    public required string Summary { get; init; }

    public string? Payload { get; init; }

    /// <summary>
    /// When this Outcome's <see cref="Payload"/> stops being retained. The payload is an ephemeral
    /// projection of Inventory state that only exists so a Participant can pick their answer back up
    /// after a disconnect; current state is authoritative, so keeping it indefinitely would grow
    /// unboundedly while serving an increasingly stale copy. Null exactly when there is no payload to
    /// retain, and null again once a cleanup pass has discarded one - the Outcome itself (its
    /// category, code, and summary) is permanent either way.
    /// </summary>
    public DateTimeOffset? PayloadExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// How long a recorded payload is retained. Long enough to survive a Participant reconnecting to
    /// an answer they were already given; short enough that a stale projection is never mistaken for
    /// current Inventory state.
    /// </summary>
    public static readonly TimeSpan PayloadRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// Records a terminal result for <paramref name="category"/>. The processing status is derived
    /// here rather than supplied, so no caller can record a semantic answer as a system failure (or
    /// the reverse) by mistake.
    /// </summary>
    public static Outcome Record(
        TurnId turnId, OutcomeCategory category, string code, string summary, DateTimeOffset createdAt, string? payload = null) =>
        new()
        {
            TurnId = turnId,
            Status = category == OutcomeCategory.TransientFailure ? OutcomeStatus.Failed : OutcomeStatus.Completed,
            Category = category,
            Code = code,
            Summary = summary,
            Payload = payload,
            PayloadExpiresAt = payload is null ? null : createdAt + PayloadRetention,
            CreatedAt = createdAt,
        };

    /// <summary>
    /// The same Outcome with its retained payload discarded. Used by scheduled cleanup once the
    /// payload has expired: the semantic answer survives, only the projection it carried is dropped.
    /// </summary>
    public Outcome WithoutRetainedPayload() => this with { Payload = null, PayloadExpiresAt = null };

    public static Outcome Completed(TurnId turnId, string code, string summary, DateTimeOffset createdAt, string? payload = null) =>
        Record(turnId, OutcomeCategory.Completed, code, summary, createdAt, payload);

    /// <summary>The system, model, or a dependency failed to produce an answer for this Turn.</summary>
    public static Outcome SystemFailure(TurnId turnId, string code, string summary, DateTimeOffset createdAt, string? payload = null) =>
        Record(turnId, OutcomeCategory.TransientFailure, code, summary, createdAt, payload);
}
