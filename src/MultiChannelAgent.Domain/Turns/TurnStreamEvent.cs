namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// The stable vocabulary of event kinds written into a Turn stream. The values are fixed so every
/// reader can recognize the same progression markers without depending on transport details or
/// renumbering already-persisted records.
/// </summary>
public enum TurnEventKind
{
    Accepted,
    Processing,
    Part,
    Outcome,
}

/// <summary>
/// The stable vocabulary for the response pieces carried by a Turn. The text/data split is fixed so
/// a consumer can distinguish human-facing content from structured payloads without guessing from
/// shape alone.
/// </summary>
public enum TurnResponsePartKind
{
    Text,
    Data,
}

/// <summary>The stable machine text exposed for <see cref="TurnEventKind"/>.</summary>
public static class TurnEventKindExtensions
{
    public static string ToMachineText(this TurnEventKind kind) => kind switch
    {
        TurnEventKind.Accepted => "accepted",
        TurnEventKind.Processing => "processing",
        TurnEventKind.Part => "part",
        TurnEventKind.Outcome => "outcome",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled turn event kind."),
    };
}

/// <summary>The stable machine text exposed for <see cref="TurnResponsePartKind"/>.</summary>
public static class TurnResponsePartKindExtensions
{
    public static string ToMachineText(this TurnResponsePartKind kind) => kind switch
    {
        TurnResponsePartKind.Text => "text",
        TurnResponsePartKind.Data => "data",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled turn response part kind."),
    };
}

/// <summary>
/// Fixed sequence numbers for the Turn stream. They are intentionally sparse so every issued event
/// keeps a stable ordering boundary even if more kinds are added later.
/// </summary>
public static class TurnEventSequence
{
    public const long Accepted = 1L;
    public const long Processing = 2L;
    public const long FirstPart = 100L;
    public const int MaxParts = 64;
    public const long Outcome = 1_000_000L;

    private const long LastPart = 163L;

    public static long ForPart(int order)
    {
        if (order < 1 || order > MaxParts)
        {
            throw new ArgumentOutOfRangeException(nameof(order), order, "Part order must be between 1 and 64.");
        }

        return FirstPart + order - 1L;
    }

    public static bool IsIssued(long sequence) =>
        sequence is Accepted or Processing or Outcome || (sequence >= FirstPart && sequence <= LastPart);
}

/// <summary>
/// A processing marker in the Turn stream. The retained window matches the outcome payload window so
/// transient progress state expires on the same cadence as the data it summarizes.
/// </summary>
public sealed record TurnProgressEvent
{
    /// <summary>
    /// How long a progress marker is retained. The window intentionally matches
    /// <see cref="Outcome.PayloadRetention"/> so stream readers and retained payloads age out
    /// together.
    /// </summary>
    public static readonly TimeSpan Retention = Outcome.PayloadRetention;

    public required TurnId TurnId { get; init; }

    public required long Sequence { get; init; }

    public required TurnEventKind Kind { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public static TurnProgressEvent Processing(TurnId turnId, DateTimeOffset occurredAt) =>
        new()
        {
            TurnId = turnId,
            Sequence = TurnEventSequence.Processing,
            Kind = TurnEventKind.Processing,
            OccurredAt = occurredAt,
            ExpiresAt = occurredAt + Retention,
        };
}
