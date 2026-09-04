using System.Text.Json;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public sealed class TurnEventReaderTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset StartedAt = new(2026, 1, 1, 0, 0, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = new(2026, 1, 1, 0, 0, 2, TimeSpan.Zero);
    private static readonly ParticipantId Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Stranger = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task Unknown_and_another_participants_turn_are_both_not_disclosed()
    {
        var (reader, inbox, _, _, _) = CreateReader();
        var turn = CreateTurn("native-1");
        await inbox.AcceptAsync(turn, CancellationToken.None);

        var unknown = await reader.ReadAfterAsync(TurnId.NewId(), Owner, 0, CancellationToken.None);
        var strangers = await reader.ReadAfterAsync(turn.TurnId, Stranger, 0, CancellationToken.None);

        Assert.Null(unknown);
        Assert.Null(strangers);
    }

    [Fact]
    public async Task Accepted_turn_projects_only_its_durable_acceptance()
    {
        var (reader, inbox, _, _, _) = CreateReader();
        var turn = CreateTurn("native-2");
        await inbox.AcceptAsync(turn, CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 0, CancellationToken.None);

        Assert.NotNull(page);
        var accepted = Assert.Single(page.Events);
        Assert.Equal(1, accepted.Sequence);
        Assert.Equal("accepted", accepted.Name);
        Assert.Equal(
            $"{{\"turnId\":\"{turn.TurnId.Value}\",\"receivedAt\":\"2026-01-01T00:00:00+00:00\"}}",
            accepted.Data);
        Assert.False(page.ReachedTerminal);
    }

    [Fact]
    public async Task Retained_processing_marker_follows_acceptance()
    {
        var (reader, inbox, progress, _, _) = CreateReader();
        var turn = CreateTurn("native-3");
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await progress.AppendAsync(TurnProgressEvent.Processing(turn.TurnId, StartedAt), CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal([1L, 2L], page.Events.Select(streamEvent => streamEvent.Sequence));
        Assert.Equal(["accepted", "processing"], page.Events.Select(streamEvent => streamEvent.Name));
        Assert.Equal(
            $"{{\"turnId\":\"{turn.TurnId.Value}\",\"startedAt\":\"2026-01-01T00:00:01+00:00\"}}",
            page.Events[1].Data);
        Assert.False(page.ReachedTerminal);
    }

    [Fact]
    public async Task Completed_turn_projects_text_data_and_terminal_events_with_exact_content()
    {
        var (reader, inbox, progress, outcomes, deliveries) = CreateReader();
        var turn = CreateTurn("native-4");
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await progress.AppendAsync(TurnProgressEvent.Processing(turn.TurnId, StartedAt), CancellationToken.None);
        await outcomes.SaveAsync(
            Outcome.Completed(
                turn.TurnId,
                "stock_listed",
                "One stock entry.",
                CompletedAt,
                """{"version":1,"kind":"stock_list"}"""),
            CancellationToken.None);
        var delivery = Delivery.Request(turn.TurnId, "web", "not exposed", CompletedAt);
        await deliveries.SaveAsync(delivery, CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(page.ReachedTerminal);
        Assert.Equal([1L, 2L, 100L, 101L, 1_000_000L], page.Events.Select(streamEvent => streamEvent.Sequence));
        Assert.Equal(["accepted", "processing", "part", "part", "outcome"], page.Events.Select(streamEvent => streamEvent.Name));
        Assert.Equal(
            $"{{\"turnId\":\"{turn.TurnId.Value}\",\"order\":1,\"kind\":\"text\",\"text\":\"One stock entry.\",\"payload\":null}}",
            page.Events[2].Data);
        Assert.Equal(
            $"{{\"turnId\":\"{turn.TurnId.Value}\",\"order\":2,\"kind\":\"data\",\"text\":null,\"payload\":{{\"version\":1,\"kind\":\"stock_list\"}}}}",
            page.Events[3].Data);
        Assert.Equal(
            $"{{\"turnId\":\"{turn.TurnId.Value}\",\"status\":\"completed\",\"category\":\"completed\",\"code\":\"stock_listed\",\"summary\":\"One stock entry.\",\"deliveries\":[{{\"deliveryId\":\"{delivery.DeliveryId}\",\"channel\":\"web\",\"status\":\"pending\",\"attempts\":0}}]}}",
            page.Events[4].Data);
    }

    [Fact]
    public async Task Outcome_without_retained_payload_projects_exactly_one_text_part()
    {
        var (reader, inbox, _, outcomes, _) = CreateReader();
        var turn = CreateTurn("native-5");
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await outcomes.SaveAsync(
            Outcome.Completed(turn.TurnId, "echoed", "Echoed answer.", CompletedAt),
            CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 2, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal([100L, 1_000_000L], page.Events.Select(streamEvent => streamEvent.Sequence));
        var part = page.Events[0];
        var data = JsonSerializer.Deserialize<TurnResponsePartData>(part.Data, JsonOptions);
        Assert.Equal(1, data!.Order);
        Assert.Equal("text", data.Kind);
        Assert.Equal("Echoed answer.", data.Text);
        Assert.Null(data.Payload);
    }

    [Fact]
    public async Task Resume_after_processing_yields_only_answer_parts_and_outcome()
    {
        var (reader, inbox, progress, outcomes, _) = CreateReader();
        var turn = CreateTurn("native-6");
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await progress.AppendAsync(TurnProgressEvent.Processing(turn.TurnId, StartedAt), CancellationToken.None);
        await outcomes.SaveAsync(
            Outcome.Completed(turn.TurnId, "listed", "Answer.", CompletedAt, """{"version":1}"""),
            CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 2, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal([100L, 101L, 1_000_000L], page.Events.Select(streamEvent => streamEvent.Sequence));
        Assert.True(page.ReachedTerminal);
    }

    [Fact]
    public async Task Resume_from_terminal_is_empty_and_remains_terminal()
    {
        var (reader, inbox, _, outcomes, _) = CreateReader();
        var turn = CreateTurn("native-7");
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await outcomes.SaveAsync(
            Outcome.Completed(turn.TurnId, "completed", "Answer.", CompletedAt),
            CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 1_000_000, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Empty(page.Events);
        Assert.True(page.ReachedTerminal);
    }

    [Fact]
    public async Task Swept_progress_does_not_prevent_terminal_answer_replay()
    {
        var (reader, inbox, progress, outcomes, _) = CreateReader();
        var turn = CreateTurn("native-8");
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await progress.AppendAsync(TurnProgressEvent.Processing(turn.TurnId, StartedAt), CancellationToken.None);
        await outcomes.SaveAsync(
            Outcome.Completed(turn.TurnId, "listed", "Retained answer.", CompletedAt, """{"version":1}"""),
            CancellationToken.None);
        await progress.DeleteExpiredAsync(StartedAt + TurnProgressEvent.Retention, 1, CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal([1L, 100L, 101L, 1_000_000L], page.Events.Select(streamEvent => streamEvent.Sequence));
        Assert.True(page.ReachedTerminal);
    }

    [Fact]
    public async Task Every_event_data_is_single_line_and_contains_only_recorded_semantic_content()
    {
        var (reader, inbox, progress, outcomes, _) = CreateReader();
        var turn = CreateTurn("native-9", "raw model token\r\nsecond token");
        await inbox.AcceptAsync(turn, CancellationToken.None);
        await progress.AppendAsync(TurnProgressEvent.Processing(turn.TurnId, StartedAt), CancellationToken.None);
        await outcomes.SaveAsync(
            Outcome.Completed(turn.TurnId, "completed", "Recorded\r\nsummary.", CompletedAt, """{"note":"line1\r\nline2"}"""),
            CancellationToken.None);

        var page = await reader.ReadAfterAsync(turn.TurnId, Owner, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.All(
            page.Events,
            streamEvent =>
            {
                Assert.DoesNotContain('\r', streamEvent.Data);
                Assert.DoesNotContain('\n', streamEvent.Data);
                Assert.DoesNotContain("raw model token", streamEvent.Data);
                Assert.DoesNotContain("second token", streamEvent.Data);
            });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static (
        TurnEventReader Reader,
        InMemoryInboxStore Inbox,
        InMemoryTurnProgressEventStore Progress,
        InMemoryOutcomeStore Outcomes,
        InMemoryDeliveryStore Deliveries) CreateReader()
    {
        var inbox = new InMemoryInboxStore();
        var progress = new InMemoryTurnProgressEventStore();
        var outcomes = new InMemoryOutcomeStore();
        var deliveries = new InMemoryDeliveryStore();
        return (new TurnEventReader(inbox, progress, outcomes, deliveries), inbox, progress, outcomes, deliveries);
    }

    private static InboundTurn CreateTurn(string nativeMessageId, string content = "hello") =>
        InboundTurn.Create(InboundTurnDraft.DirectText(
            nativeMessageId,
            Owner,
            "conversation-1",
            "web",
            ChannelPrincipal.EntraUser(Owner.Value.ToString(), "33333333-3333-3333-3333-333333333333"),
            ChannelCapabilities.Text | ChannelCapabilities.ProgressEvents,
            content,
            "en",
            ReceivedAt,
            "trace-1"));
}
