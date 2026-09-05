using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// One browser profile with several tabs open, against the real HTTP application boundary backed by
/// SQLite (fast, Docker-free).
///
/// Everything #35 promises about tabs, refreshes, and restarts reduces to one question: do two
/// clients that share the browser profile's cookies share one ChannelConversation, one FIFO queue,
/// and one view of every Turn in it? A page refresh, a browser restart, and a second tab are all the
/// same thing to the server - a new client presenting the same 400-day web conversation cookie - so
/// proving it for a second tab proves it for all three.
/// </summary>
public sealed class SharedBrowserProfileScenario : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private SqliteWebApplicationFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SqliteWebApplicationFactory();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Two_tabs_of_one_browser_profile_share_exactly_one_channel_conversation()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Multi Tab Participant");
        await firstTab.CreateAndSelectInventoryAsync("Shared Tab Warehouse");
        var secondTab = firstTab.OpenAnotherTab();

        await firstTab.SubmitAcceptedTurnAsync("native-tab-1", "list stock");
        await secondTab.SubmitAcceptedTurnAsync("native-tab-2", "list stock");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var conversations = await db.InboxEntries.AsNoTracking()
            .Select(e => e.ChannelConversationId)
            .Distinct()
            .ToListAsync();

        Assert.Single(conversations);
    }

    [Fact]
    public async Task Turns_submitted_from_different_tabs_queue_in_one_shared_first_in_first_out_order()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Queueing Participant");
        await firstTab.CreateAndSelectInventoryAsync("Queued Warehouse");
        var secondTab = firstTab.OpenAnotherTab();

        var first = await firstTab.SubmitAcceptedTurnAsync("native-fifo-1", "add stock Steel Bolts quantity 1");
        var second = await secondTab.SubmitAcceptedTurnAsync("native-fifo-2", "add stock Steel Bolts quantity 1");
        var third = await firstTab.SubmitAcceptedTurnAsync("native-fifo-3", "add stock Steel Bolts quantity 1");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var order = await db.InboxEntries.AsNoTracking()
            .OrderBy(e => e.ConversationSequence)
            .Select(e => e.TurnId)
            .ToListAsync();

        Assert.Equal(new[] { first, second, third }, order);
    }

    [Fact]
    public async Task A_second_tab_can_watch_a_turn_the_first_tab_submitted_through_its_outcome()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Watching Tabs Participant");
        await firstTab.CreateAndSelectInventoryAsync("Watched Tab Warehouse");
        var secondTab = firstTab.OpenAnotherTab();

        var turnId = await firstTab.SubmitAcceptedTurnAsync("native-tab-watch", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);
        using var response = await secondTab.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        await using var reader = await ServerSentEventReader.OpenAsync(response, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await reader.ReadAsync(5, timeout.Token);
        Assert.Equal("outcome", events[^1].Name);
    }

    [Fact]
    public async Task Resubmitting_the_same_native_message_returns_the_same_turn_and_mutates_stock_once()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Recovering Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Recovered Warehouse");

        var first = await participant.SubmitAcceptedTurnAsync(
            "native-recover-1",
            "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        using var resubmission = await participant.SubmitTurnAsync(
            "native-recover-1",
            "add stock Steel Bolts quantity 4");

        Assert.Equal(HttpStatusCode.OK, resubmission.StatusCode);
        var recorded = await resubmission.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first, recorded.GetProperty("turnId").GetGuid());

        await ProcessUntilQuietAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        Assert.Equal(1, await db.InboxEntries.AsNoTracking().CountAsync());

        var entry = await db.StockEntries.AsNoTracking().SingleAsync(e => e.InventoryId == inventoryId);
        Assert.Equal(4m, entry.Quantity);
    }

    [Fact]
    public async Task Reconnecting_to_a_turn_replays_identical_id_name_and_data_events()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Replaying Participant");
        await participant.CreateAndSelectInventoryAsync("Replayed Warehouse");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-replay-1", "list stock");
        await ProcessUntilQuietAsync();

        using var timeout = new CancellationTokenSource(ReadTimeout);

        using var firstConnection = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        Assert.Equal(HttpStatusCode.OK, firstConnection.StatusCode);
        Assert.Equal("text/event-stream", firstConnection.Content.Headers.ContentType!.MediaType);
        await using var firstReader = await ServerSentEventReader.OpenAsync(firstConnection, timeout.Token);
        var firstEvents = await firstReader.ReadAsync(5, timeout.Token);
        Assert.Equal(5, firstEvents.Count);
        Assert.Equal(
            ["accepted", "processing", "part", "part", "outcome"],
            firstEvents.Select(e => e.Name));

        using var secondConnection = await participant.OpenTurnStreamAsync(turnId, cancellationToken: timeout.Token);
        Assert.Equal(HttpStatusCode.OK, secondConnection.StatusCode);
        Assert.Equal("text/event-stream", secondConnection.Content.Headers.ContentType!.MediaType);
        await using var secondReader = await ServerSentEventReader.OpenAsync(secondConnection, timeout.Token);
        var secondEvents = await secondReader.ReadAsync(5, timeout.Token);
        Assert.Equal(5, secondEvents.Count);
        Assert.Equal(
            ["accepted", "processing", "part", "part", "outcome"],
            secondEvents.Select(e => e.Name));

        Assert.Equal(
            firstEvents.Select(e => (e.Id, e.Name, e.Data)),
            secondEvents.Select(e => (e.Id, e.Name, e.Data)));
    }

    [Fact]
    public async Task Browser_restart_cookie_is_secure_httponly_and_persists_for_about_400_days()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var observedAt = DateTimeOffset.UtcNow;
        string? setCookieHeader = null;

        await ConversationTestClient.SignInAsync(
            http,
            "Restarting Participant",
            bootstrapResponse =>
            {
                setCookieHeader = bootstrapResponse.Headers
                    .GetValues("Set-Cookie")
                    .Single(header => header.StartsWith("mca_web_conversation=", StringComparison.OrdinalIgnoreCase));
            });

        Assert.NotNull(setCookieHeader);
        var attributes = setCookieHeader.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToArray();
        Assert.Contains(attributes, attribute => attribute.Equals("HttpOnly", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(attributes, attribute => attribute.Equals("Secure", StringComparison.OrdinalIgnoreCase));

        var maxAgeValue = CookieAttributeValue(attributes, "Max-Age");
        var expiresValue = CookieAttributeValue(attributes, "Expires");
        TimeSpan? persistence = null;

        if (maxAgeValue is not null)
        {
            Assert.True(
                long.TryParse(maxAgeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAgeSeconds),
                $"The Max-Age cookie attribute was not an integer: {maxAgeValue}");
            persistence = TimeSpan.FromSeconds(maxAgeSeconds);
        }
        else if (expiresValue is not null)
        {
            Assert.True(
                DateTimeOffset.TryParse(
                    expiresValue,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expiresAt),
                $"The Expires cookie attribute was not an HTTP date: {expiresValue}");
            persistence = expiresAt - observedAt;
        }

        Assert.True(persistence.HasValue, "The web conversation cookie must have an Expires or Max-Age attribute.");
        Assert.InRange(persistence.GetValueOrDefault().TotalDays, 390, 410);
    }

    [Fact]
    public async Task Browsing_inventory_list_other_stock_and_units_never_switches_the_active_inventory()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Browsing Participant");
        var active = await participant.CreateAndSelectInventoryAsync("Active Warehouse");
        var other = await participant.CreateAndSelectInventoryAsync("Other Warehouse");
        await participant.SelectInventoryAsync(active);

        using var inventoryListResponse = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/inventories"));
        using var stockResponse = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{other}/stock"));
        using var unitsResponse = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{other}/units"));

        Assert.Equal(HttpStatusCode.OK, inventoryListResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stockResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unitsResponse.StatusCode);

        var bootstrap = (await participant.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(active.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());
    }

    [Fact]
    public async Task Selecting_an_inventory_switches_the_active_inventory_and_records_the_durable_selection()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Switching Participant");
        var first = await participant.CreateAndSelectInventoryAsync("First Warehouse");
        var second = await participant.CreateAndSelectInventoryAsync("Second Warehouse");

        await participant.SelectInventoryAsync(first);
        await participant.SelectInventoryAsync(second);

        var bootstrap = (await participant.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(second.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var selection = await db.ActiveInventorySelections.AsNoTracking().SingleAsync();
        Assert.Equal(second, selection.InventoryId);
    }

    [Fact]
    public async Task A_switch_in_one_tab_is_observed_by_the_other_tabs_bootstrap()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var firstTab = await ConversationTestClient.SignInAsync(http, "Sharing Participant");
        var first = await firstTab.CreateAndSelectInventoryAsync("Tab One Warehouse");
        var second = await firstTab.CreateAndSelectInventoryAsync("Tab Two Warehouse");
        await firstTab.SelectInventoryAsync(first);

        var secondTab = firstTab.OpenAnotherTab();
        await secondTab.SelectInventoryAsync(second);

        var bootstrap = (await firstTab.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(second.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());
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

    private static string? CookieAttributeValue(IEnumerable<string> attributes, string name)
    {
        foreach (var attribute in attributes)
        {
            var separator = attribute.IndexOf('=');
            if (separator > 0 && attribute[..separator].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return attribute[(separator + 1)..];
            }
        }

        return null;
    }
}
