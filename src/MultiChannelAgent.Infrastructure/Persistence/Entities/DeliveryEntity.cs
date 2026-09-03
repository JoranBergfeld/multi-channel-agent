namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable outbox row for one requested Delivery. Dispatched and retried independently of Turn
/// processing so sending never reruns model planning or mutation.
/// </summary>
public sealed class DeliveryEntity
{
    public Guid DeliveryId { get; set; }

    public Guid TurnId { get; set; }

    public required string Channel { get; set; }

    public required string Payload { get; set; }

    public DeliveryEntityStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }
}

public enum DeliveryEntityStatus
{
    Pending = 0,
    Delivered = 1,
}
