using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class TurnOutcomeReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static async Task<(InMemoryInboxStore Inbox, InMemoryOutcomeStore Outcomes, InMemoryDeliveryStore Deliveries, TurnOutcomeReader Reader, TurnId TurnId)>
        SeedProcessedTurnAsync()
    {
        var inbox = new InMemoryInboxStore();
        var outcomeStore = new InMemoryOutcomeStore();
        var deliveryStore = new InMemoryDeliveryStore();
        var turn = InboundTurn.Create("native-1", Owner, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await outcomeStore.SaveAsync(Outcome.Completed(turn.TurnId, "echoed", "Echoed: hello", Now), CancellationToken.None);
        var delivery = Delivery.Request(turn.TurnId, "synthetic", "Echoed: hello", Now);
        await deliveryStore.SaveAsync(delivery, CancellationToken.None);
        var reader = new TurnOutcomeReader(inbox, outcomeStore, deliveryStore);

        return (inbox, outcomeStore, deliveryStore, reader, turn.TurnId);
    }

    [Fact]
    public async Task Unknown_or_not_yet_processed_turn_returns_null()
    {
        var reader = new TurnOutcomeReader(new InMemoryInboxStore(), new InMemoryOutcomeStore(), new InMemoryDeliveryStore());

        var view = await reader.GetAsync(TurnId.NewId(), Owner, CancellationToken.None);

        Assert.Null(view);
    }

    [Fact]
    public async Task Processed_turn_exposes_terminal_outcome_and_its_deliveries_to_its_own_participant()
    {
        var (_, _, _, reader, turnId) = await SeedProcessedTurnAsync();

        var view = await reader.GetAsync(turnId, Owner, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(turnId, view!.TurnId);
        Assert.Equal("completed", view.Status);
        Assert.Equal("echoed", view.Code);
        Assert.Equal("Echoed: hello", view.Summary);
        var deliveryView = Assert.Single(view.Deliveries);
        Assert.Equal("synthetic", deliveryView.Channel);
        Assert.Equal("pending", deliveryView.Status);
    }

    [Fact]
    public async Task Reading_another_participants_turn_returns_null_instead_of_disclosing_it()
    {
        var (_, _, _, reader, turnId) = await SeedProcessedTurnAsync();

        var view = await reader.GetAsync(turnId, Stranger, CancellationToken.None);

        Assert.Null(view);
    }

    [Fact]
    public async Task An_outcome_with_no_payload_exposes_a_null_payload()
    {
        var (_, _, _, reader, turnId) = await SeedProcessedTurnAsync();

        var view = await reader.GetAsync(turnId, Owner, CancellationToken.None);

        Assert.Null(view!.Payload);
    }

    [Fact]
    public async Task An_outcome_carrying_a_json_payload_exposes_it_as_parsed_json()
    {
        var inbox = new InMemoryInboxStore();
        var outcomeStore = new InMemoryOutcomeStore();
        var deliveryStore = new InMemoryDeliveryStore();
        var turn = InboundTurn.Create("native-2", Owner, "conversation-1", "list stock", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await outcomeStore.SaveAsync(
            Outcome.Completed(turn.TurnId, "completed", "1 Stock Entry found.", Now, """{"version":1,"kind":"stock_list"}"""),
            CancellationToken.None);
        var reader = new TurnOutcomeReader(inbox, outcomeStore, deliveryStore);

        var view = await reader.GetAsync(turn.TurnId, Owner, CancellationToken.None);

        Assert.NotNull(view!.Payload);
        Assert.Equal(1, view.Payload!.Value.GetProperty("version").GetInt32());
        Assert.Equal("stock_list", view.Payload.Value.GetProperty("kind").GetString());
    }
}
