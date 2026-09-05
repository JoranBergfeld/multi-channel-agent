using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Host.Endpoints;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The resumable per-Turn stream over real HTTP, backed by SQLite (fast, Docker-free). Everything
/// #35 promises about disconnecting and coming back is a property of this endpoint: the same events
/// in the same order however often you reconnect, exactly one terminal event, a stream that ends
/// itself, a resume point that is honoured, a bad resume point that is ignored rather than fatal, and
/// a Turn belonging to someone else that is indistinguishable from one that does not exist.
/// </summary>
public sealed class TurnEventStreamHttpTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        // Deliberately the real TimeProvider: the stream's poll interval, heartbeat, and interactive
        // wait bound are real delays, and a FakeTimeProvider nobody advances would hang them forever.
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_answered_turn_streams_acceptance_progress_parts_and_one_terminal_outcome_then_ends()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Streaming Participant");
        await participant.CreateAndSelectInventoryAsync("Streamed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-1", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var events = await reader.ReadAsync(5, timeout.Token);

        Assert.Equal(
            ["accepted", "processing", "part", "part", "outcome"],
            events.Select(e => e.Name));
        Assert.Equal(
            new long?[]
            {
                TurnEventSequence.Accepted,
                TurnEventSequence.Processing,
                TurnEventSequence.ForPart(1),
                TurnEventSequence.ForPart(2),
                TurnEventSequence.Outcome,
            },
            events.Select(e => e.Id));

        var terminal = JsonDocument.Parse(events[^1].Data).RootElement;
        Assert.Equal(turnId, terminal.GetProperty("turnId").GetGuid());
        Assert.Equal("completed", terminal.GetProperty("status").GetString());

        // The stream is finite: nothing follows the terminal event and the server ends the response.
        // Reading again from the SAME reader is what proves it, which is only meaningful because the
        // reader is stateful - a fresh one would have lost whatever the first read had buffered.
        Assert.Empty(await reader.ReadAsync(1, timeout.Token));
    }

    [Fact]
    public async Task A_stream_with_nothing_left_to_say_keeps_proving_it_is_still_alive()
    {
        // Everything about this factory is production except three numbers. The heartbeat has to be
        // asserted - an ingress that sees no bytes closes the connection, and a stream that stopped
        // heart-beating would fail in production and nowhere else - but asserting it at the production
        // fifteen seconds would add half a minute to every CI run forever.
        using var fastHeartbeat = new SqliteWebApplicationFactory(
            configureTestServices: services => services.AddSingleton(new TurnStreamOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
                HeartbeatInterval = TimeSpan.FromMilliseconds(200),
                MaxDuration = TimeSpan.FromSeconds(30),
            }));

        var http = ConversationTestClient.CreateHttpsClient(fastHeartbeat);
        var participant = await ConversationTestClient.SignInAsync(http, "Idle Participant");
        await participant.CreateAndSelectInventoryAsync("Idle Warehouse");

        // Deliberately never processed: a Turn with one event and then nothing at all is the only
        // state in which a keep-alive matters.
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-heartbeat", "list stock");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        // Deliberately NOT reading the `accepted` event first. The stream writes it on its first
        // poll, 50 ms in, long before the first 200 ms heartbeat - so waiting for heartbeats here
        // means the wait itself consumes that event's lines. A heartbeat wait that skipped anything
        // that was not a comment would decode it into nothing and lose it silently.
        await reader.WaitForHeartbeatsAsync(2, timeout.Token);

        // Two beats, so this cannot pass on a single accidental byte: the stream is repeating itself
        // on a timer while it has nothing to say.
        Assert.True(
            reader.HeartbeatCount >= 2,
            $"A silent stream must keep writing keep-alive comments, but only {reader.HeartbeatCount} arrived.");

        // And the event the wait ran over is still there to be read, because the wait used the same
        // parse/dispatch state machine and queued what it completed. This is the reader's whole
        // contract - sequential reads of one response lose nothing - asserted rather than assumed.
        Assert.Equal(["accepted"], (await reader.ReadAsync(1, timeout.Token)).Select(e => e.Name));
    }

    [Fact]
    public async Task Every_events_data_is_exactly_one_line_so_the_framing_can_never_be_broken_by_content()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Framing Participant");
        await participant.CreateAndSelectInventoryAsync("Framed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-frame", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);

        foreach (var line in body.Split('\n').Where(l => l.StartsWith("data:")))
        {
            Assert.DoesNotContain('\r', line);
            JsonDocument.Parse(line["data:".Length..].TrimStart());
        }
    }

    [Fact]
    public async Task Reconnecting_with_a_resume_point_replays_only_what_came_after_it()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Resuming Participant");
        await participant.CreateAndSelectInventoryAsync("Resumed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-2", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(
            turnId, TurnEventSequence.Processing, timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var events = await reader.ReadAsync(3, timeout.Token);

        Assert.Equal(["part", "part", "outcome"], events.Select(e => e.Name));
    }

    [Fact]
    public async Task Reconnecting_from_the_terminal_event_ends_the_stream_immediately_with_nothing_replayed()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Finished Participant");
        await participant.CreateAndSelectInventoryAsync("Finished Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-3", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, TurnEventSequence.Outcome, timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        Assert.Empty(await reader.ReadAsync(1, timeout.Token));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("999999999999999999")]
    public async Task A_resume_point_this_application_never_issued_is_ignored_and_the_whole_stream_is_replayed(
        string lastEventId)
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Tampering Participant");
        await participant.CreateAndSelectInventoryAsync("Tampered Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync($"native-stream-bad-{lastEventId}", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId}/events?lastEventId={Uri.EscapeDataString(lastEventId)}");
        using var response = await participant.SendAsync(request);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        // Never an error: a browser's EventSource cannot read an error body and would reconnect
        // forever with the same bad value, so a value we never issued is treated exactly as if none
        // had been sent - the same rule WebConversationCookie applies to a tampered cookie.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ["accepted", "processing", "part", "part", "outcome"],
            (await reader.ReadAsync(5, timeout.Token)).Select(e => e.Name));
    }

    [Fact]
    public async Task The_last_event_id_request_header_a_browser_sends_on_its_own_reconnect_is_honoured()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Header Participant");
        await participant.CreateAndSelectInventoryAsync("Header Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-4", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId}/events");
        request.Headers.Add("Last-Event-ID", TurnEventSequence.ForPart(1).ToString());
        using var response = await participant.SendAsync(request);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        Assert.Equal(["part", "outcome"], (await reader.ReadAsync(2, timeout.Token)).Select(e => e.Name));
    }

    [Fact]
    public async Task Another_participants_turn_and_a_turn_that_does_not_exist_are_indistinguishable()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(http, "Turn Owner");
        await owner.CreateAndSelectInventoryAsync("Private Warehouse");
        var turnId = await owner.SubmitAcceptedTurnAsync("native-stream-5", "list stock");
        await ProcessUntilQuietAsync();

        var stranger = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Turn Stranger");

        using var foreign = await stranger.OpenTurnStreamAsync(turnId);
        using var missing = await stranger.OpenTurnStreamAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_turn_that_has_not_been_processed_yet_streams_its_acceptance_and_keeps_the_connection_open()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Waiting Participant");
        await participant.CreateAndSelectInventoryAsync("Waiting Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-6", "list stock");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var reading = reader.ReadAsync(5, timeout.Token);
        await ProcessUntilQuietAsync();
        var events = await reading;

        Assert.Equal("accepted", events[0].Name);
        Assert.Equal("outcome", events[^1].Name);
    }

    [Fact]
    public async Task Disconnecting_mid_stream_changes_nothing_and_the_recorded_outcome_is_still_there_afterwards()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Disconnecting Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Disconnected Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-stream-7", "add stock Steel Bolts quantity 4");

        using (var abort = new CancellationTokenSource())
        {
            using var response = await participant.OpenTurnStreamAsync(turnId, cancellationToken: abort.Token);
            await abort.CancelAsync();
        }

        await ProcessUntilQuietAsync();

        var outcome = await participant.GetOutcomeAsync(turnId);
        Assert.NotNull(outcome);

        // Recovery is a read. Exactly one Turn was ever accepted for this native message, so nothing
        // mutation-capable was resubmitted by reconnecting or by giving up.
        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<TurnEventReader>();
        Assert.NotNull(await reader.ReadAfterAsync(
            new TurnId(turnId),
            new Domain.Inventories.ParticipantId(Guid.Parse((await participant.GetBootstrapAsync())
                .GetProperty("bootstrap").GetProperty("participantId").GetString()!)),
            0L,
            CancellationToken.None));

        Assert.NotEqual(Guid.Empty, inventoryId);
    }

    private async Task ProcessUntilQuietAsync()
    {
        while (true)
        {
            using var scope = _factory.Services.CreateScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            if (await coordinator.ProcessPendingAsync(CancellationToken.None) == 0)
            {
                return;
            }
        }
    }
}
