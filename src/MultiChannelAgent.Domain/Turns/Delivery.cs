namespace MultiChannelAgent.Domain.Turns;

/// <summary>Delivery lifecycle status. Delivery retries never rerun Turn processing or mutation.</summary>
public enum DeliveryStatus
{
    Pending,
    Delivered,
}

/// <summary>
/// A requested Delivery produced while processing a Turn (the durable outbox record). Delivery is
/// dispatched and retried independently of Turn processing: a failed send only increments
/// <see cref="Attempts"/> and stays <see cref="DeliveryStatus.Pending"/> for the delivery worker to retry.
/// </summary>
public sealed record Delivery
{
    public required Guid DeliveryId { get; init; }

    public required TurnId TurnId { get; init; }

    public required string Channel { get; init; }

    public required string Payload { get; init; }

    public required DeliveryStatus Status { get; init; }

    public required int Attempts { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    public static Delivery Request(TurnId turnId, string channel, string payload, DateTimeOffset createdAt) =>
        new()
        {
            DeliveryId = Guid.NewGuid(),
            TurnId = turnId,
            Channel = channel,
            Payload = payload,
            Status = DeliveryStatus.Pending,
            Attempts = 0,
            CreatedAt = createdAt,
            DeliveredAt = null,
        };

    public Delivery MarkDelivered(DateTimeOffset deliveredAt) => this with
    {
        Status = DeliveryStatus.Delivered,
        Attempts = Attempts + 1,
        DeliveredAt = deliveredAt,
    };

    public Delivery MarkAttemptFailed() => this with
    {
        Attempts = Attempts + 1,
    };
}
