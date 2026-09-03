using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class TurnOutcomeReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unknown_or_not_yet_processed_turn_returns_null()
    {
        var reader = new TurnOutcomeReader(new InMemoryOutcomeStore(), new InMemoryDeliveryStore());

        var view = await reader.GetAsync(TurnId.NewId(), CancellationToken.None);

        Assert.Null(view);
    }

    [Fact]
    public async Task Processed_turn_exposes_terminal_outcome_and_its_deliveries()
    {
        var outcomeStore = new InMemoryOutcomeStore();
        var deliveryStore = new InMemoryDeliveryStore();
        var turnId = TurnId.NewId();
        await outcomeStore.SaveAsync(Outcome.Completed(turnId, "echoed", "Echoed: hello", Now), CancellationToken.None);
        var delivery = Delivery.Request(turnId, "synthetic", "Echoed: hello", Now);
        await deliveryStore.SaveAsync(delivery, CancellationToken.None);
        var reader = new TurnOutcomeReader(outcomeStore, deliveryStore);

        var view = await reader.GetAsync(turnId, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(turnId, view!.TurnId);
        Assert.Equal("completed", view.Status);
        Assert.Equal("echoed", view.Code);
        Assert.Equal("Echoed: hello", view.Summary);
        var deliveryView = Assert.Single(view.Deliveries);
        Assert.Equal(delivery.DeliveryId, deliveryView.DeliveryId);
        Assert.Equal("synthetic", deliveryView.Channel);
        Assert.Equal("pending", deliveryView.Status);
    }
}
