using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests;

public class DeliveryTests
{
    [Fact]
    public void Requested_delivery_starts_pending_with_zero_attempts()
    {
        var turnId = TurnId.NewId();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var delivery = Delivery.Request(turnId, channel: "synthetic", payload: "Echoed: hello", createdAt);

        Assert.Equal(turnId, delivery.TurnId);
        Assert.Equal("synthetic", delivery.Channel);
        Assert.Equal("Echoed: hello", delivery.Payload);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.Attempts);
        Assert.Null(delivery.DeliveredAt);
        Assert.NotEqual(default, delivery.DeliveryId);
    }

    [Fact]
    public void Marking_delivered_records_status_attempt_and_timestamp()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "synthetic", "payload", DateTimeOffset.UtcNow);
        var deliveredAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero);

        var delivered = delivery.MarkDelivered(deliveredAt);

        Assert.Equal(DeliveryStatus.Delivered, delivered.Status);
        Assert.Equal(1, delivered.Attempts);
        Assert.Equal(deliveredAt, delivered.DeliveredAt);
    }

    [Fact]
    public void Marking_failed_increments_attempts_but_keeps_pending_for_retry()
    {
        var delivery = Delivery.Request(TurnId.NewId(), "synthetic", "payload", DateTimeOffset.UtcNow);

        var afterFirstFailure = delivery.MarkAttemptFailed();

        Assert.Equal(DeliveryStatus.Pending, afterFirstFailure.Status);
        Assert.Equal(1, afterFirstFailure.Attempts);
        Assert.Null(afterFirstFailure.DeliveredAt);
    }
}
