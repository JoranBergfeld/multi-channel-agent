using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The Participant-level invalidation stream over real HTTP, backed by SQLite (fast, Docker-free).
/// The behaviour #35 asks for is "projections are invalidated after changes from any channel", so
/// these tests deliberately make the change through a different path than the one watching: a Turn
/// processed by the conversational worker, and a governance call made over HTTP.
/// </summary>
public sealed class InventoryEventStreamHttpTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        // Deliberately the real TimeProvider: the stream's poll interval and interactive wait bound
        // are real delays, and a FakeTimeProvider nobody advances would hang them forever.
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Connecting_immediately_reports_the_current_version_of_every_authorized_inventory()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Watching Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Watched Warehouse");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenInventoryStreamAsync(timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var snapshot = Assert.Single(await reader.ReadAsync(1, timeout.Token));
        Assert.Equal("snapshot", snapshot.Name);

        // No issued identity, because this stream implements no cursor. See D5: what a client needs is
        // a function of current state, so a snapshot supersedes any resume point, and advertising an
        // `id` would promise semantics the server would silently ignore.
        Assert.Null(snapshot.Id);

        var reported = Assert.Single(JsonDocument.Parse(snapshot.Data).RootElement.GetProperty("inventories").EnumerateArray());
        Assert.Equal(inventoryId, reported.GetProperty("inventoryId").GetGuid());

        // Creating an Inventory writes no audit fact, so it starts at zero and is first reported the
        // moment it appears in this Participant's authorized set.
        Assert.Equal(0L, reported.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_change_made_while_nothing_was_connected_is_in_the_next_connections_snapshot()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Reconnecting Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Reconnected Warehouse");

        using var timeout = new CancellationTokenSource(ReadTimeout);

        long firstSeenVersion;
        using (var firstConnection = await participant.OpenInventoryStreamAsync(timeout.Token))
        {
            await using var firstReader = await ServerSentEventReader.OpenAsync(firstConnection, timeout.Token);
            var snapshot = Assert.Single(await firstReader.ReadAsync(1, timeout.Token));
            firstSeenVersion = JsonDocument.Parse(snapshot.Data).RootElement
                .GetProperty("inventories").EnumerateArray().Single().GetProperty("version").GetInt64();
        }

        // Nothing is connected. This is precisely the window a cursor would exist to cover.
        await participant.SubmitAcceptedTurnAsync("native-invalidate-offline", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        using var secondConnection = await participant.OpenInventoryStreamAsync(timeout.Token);
        await using var secondReader = await ServerSentEventReader.OpenAsync(secondConnection, timeout.Token);
        var reconnected = Assert.Single(await secondReader.ReadAsync(1, timeout.Token));

        var reported = JsonDocument.Parse(reconnected.Data).RootElement
            .GetProperty("inventories").EnumerateArray().Single();

        // The change made while disconnected is not lost, and it did not need a Last-Event-ID to
        // survive: the snapshot IS the resume, because what the client needs is current state.
        Assert.Equal(inventoryId, reported.GetProperty("inventoryId").GetGuid());
        Assert.True(
            reported.GetProperty("version").GetInt64() > firstSeenVersion,
            "A reconnect must observe the change that happened while nothing was connected.");
        Assert.Null(reconnected.Id);
    }

    [Fact]
    public async Task A_change_made_through_the_conversation_invalidates_the_watching_tabs_projection()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Conversing Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Conversed Warehouse");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await participant.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var reading = reader.ReadAsync(2, timeout.Token);

        await participant.SubmitAcceptedTurnAsync("native-invalidate-1", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        var events = await reading;

        Assert.Equal(["snapshot", "changed"], events.Select(e => e.Name));
        var changed = JsonDocument.Parse(events[1].Data).RootElement;
        Assert.Equal(inventoryId, changed.GetProperty("inventoryId").GetGuid());
        Assert.True(changed.GetProperty("version").GetInt64() > 0L);
    }

    [Fact]
    public async Task A_change_made_by_another_participant_over_http_invalidates_this_participants_projection()
    {
        var ownerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Granting Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Shared Warehouse");

        var editorHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var editor = await ConversationTestClient.SignInAsync(editorHttp, "Watching Editor");
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");

        // The grant is itself an audited change, so the Editor's first snapshot starts at whatever
        // version that left behind rather than at zero. What this test is about is the NEXT change.
        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await editor.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var reading = reader.ReadAsync(2, timeout.Token);

        await owner.SubmitAcceptedTurnAsync("native-invalidate-2", "add stock Brass Rivets quantity 2");
        await ProcessUntilQuietAsync();

        var events = await reading;

        Assert.Equal(["snapshot", "changed"], events.Select(e => e.Name));
        Assert.Equal(inventoryId, JsonDocument.Parse(events[1].Data).RootElement.GetProperty("inventoryId").GetGuid());
    }

    [Fact]
    public async Task Losing_access_to_an_inventory_is_reported_so_the_projection_stops_being_shown()
    {
        var ownerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Revoking Owner");
        var inventoryId = await owner.CreateAndSelectInventoryAsync("Revoked Warehouse");

        var editorHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var editor = await ConversationTestClient.SignInAsync(editorHttp, "Revoked Editor");
        await owner.GrantMembershipAsync(inventoryId, editor.ParticipantIdentifier, "Editor");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await editor.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);
        var reading = reader.ReadAsync(2, timeout.Token);

        var removal = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/inventories/{inventoryId}/members/{editor.ParticipantIdentifier}");
        var removalResponse = await owner.SendAsync(removal, withCsrf: true);
        Assert.True(removalResponse.IsSuccessStatusCode, $"Removing the member failed with {removalResponse.StatusCode}.");

        var events = await reading;

        Assert.Equal(["snapshot", "revoked"], events.Select(e => e.Name));
        Assert.Equal(inventoryId, JsonDocument.Parse(events[1].Data).RootElement.GetProperty("inventoryId").GetGuid());
    }

    [Fact]
    public async Task A_participant_only_ever_sees_their_own_inventories_on_this_stream()
    {
        var ownerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var owner = await ConversationTestClient.SignInAsync(ownerHttp, "Private Owner");
        await owner.CreateAndSelectInventoryAsync("Nobody Elses Warehouse");

        var strangerHttp = ConversationTestClient.CreateHttpsClient(_factory);
        var stranger = await ConversationTestClient.SignInAsync(strangerHttp, "Unrelated Participant");

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await stranger.OpenInventoryStreamAsync(timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        var snapshot = Assert.Single(await reader.ReadAsync(1, timeout.Token));
        Assert.Empty(JsonDocument.Parse(snapshot.Data).RootElement.GetProperty("inventories").EnumerateArray());
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
