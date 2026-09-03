using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class DeliveryDispatchCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Dispatching_a_pending_delivery_that_succeeds_marks_it_delivered()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var deliveryStore = new InMemoryDeliveryStore();
        var delivery = Delivery.Request(TurnId.NewId(), "synthetic", "payload", Now);
        await deliveryStore.SaveAsync(delivery, CancellationToken.None);
        var sender = new ScriptedDeliverySender(succeeds: true);
        var coordinator = new DeliveryDispatchCoordinator(
            deliveryStore, new InMemoryLeaseCoordinator(timeProvider), sender, timeProvider);

        var dispatchedCount = await coordinator.DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(1, dispatchedCount);
        var stored = Assert.Single(deliveryStore.Deliveries);
        Assert.Equal(DeliveryStatus.Delivered, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.NotNull(stored.DeliveredAt);
    }

    [Fact]
    public async Task Dispatching_a_pending_delivery_that_fails_keeps_it_pending_for_retry()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var deliveryStore = new InMemoryDeliveryStore();
        var delivery = Delivery.Request(TurnId.NewId(), "synthetic", "payload", Now);
        await deliveryStore.SaveAsync(delivery, CancellationToken.None);
        var sender = new ScriptedDeliverySender(succeeds: false);
        var coordinator = new DeliveryDispatchCoordinator(
            deliveryStore, new InMemoryLeaseCoordinator(timeProvider), sender, timeProvider);

        await coordinator.DispatchPendingAsync(CancellationToken.None);

        var stored = Assert.Single(deliveryStore.Deliveries);
        Assert.Equal(DeliveryStatus.Pending, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Null(stored.DeliveredAt);
    }

    [Fact]
    public async Task With_no_pending_deliveries_dispatch_reports_zero_without_error()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var deliveryStore = new InMemoryDeliveryStore();
        var sender = new ScriptedDeliverySender(succeeds: true);
        var coordinator = new DeliveryDispatchCoordinator(
            deliveryStore, new InMemoryLeaseCoordinator(timeProvider), sender, timeProvider);

        var dispatchedCount = await coordinator.DispatchPendingAsync(CancellationToken.None);

        Assert.Equal(0, dispatchedCount);
    }
}
