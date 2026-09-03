using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Docker-free HTTP-boundary coverage of at-least-once redelivery: a duplicate submission of an
/// already-answered Turn returns that Turn's recorded terminal Outcome directly, so a channel adapter
/// (or a reconnecting browser) never has to poll for a result the application already has, while a
/// duplicate of a Turn still being processed stays an acknowledgement of accepted work.
/// </summary>
public sealed class DuplicateTurnSubmissionTests : IAsyncLifetime
{
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
    public async Task A_duplicate_of_an_answered_turn_returns_its_recorded_terminal_outcome()
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(httpClient, "Redelivered Participant");
        await participant.CreateAndSelectInventoryAsync("Redelivery Warehouse");

        var turnId = await participant.SubmitAcceptedTurnAsync("native-duplicate-1", "list stock");
        Assert.Equal(1, await ProcessPendingAsync());
        var recorded = (await participant.GetOutcomeAsync(turnId))!.Value;

        var duplicate = await participant.SubmitTurnAsync("native-duplicate-1", "list stock");

        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turnId, body.GetProperty("turnId").GetGuid());
        Assert.Equal(recorded.GetProperty("status").GetString(), body.GetProperty("status").GetString());
        Assert.Equal(recorded.GetProperty("category").GetString(), body.GetProperty("category").GetString());
        Assert.Equal(recorded.GetProperty("code").GetString(), body.GetProperty("code").GetString());
        Assert.Equal(recorded.GetProperty("summary").GetString(), body.GetProperty("summary").GetString());
        Assert.Equal("stock_list", body.GetProperty("payload").GetProperty("kind").GetString());

        // The duplicate is answered from the record, never by reprocessing.
        Assert.Equal(0, await ProcessPendingAsync());
    }

    [Fact]
    public async Task A_duplicate_of_a_turn_that_has_not_been_answered_yet_is_still_only_acknowledged()
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(httpClient, "Pending Participant");
        await participant.CreateAndSelectInventoryAsync("Pending Warehouse");

        var turnId = await participant.SubmitAcceptedTurnAsync("native-duplicate-2", "list stock");

        var duplicate = await participant.SubmitTurnAsync("native-duplicate-2", "list stock");

        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turnId, body.GetProperty("turnId").GetGuid());
        Assert.True(body.GetProperty("alreadyAccepted").GetBoolean());

        // Still exactly one Turn to process, once.
        Assert.Equal(1, await ProcessPendingAsync());
    }

    // A first submission is never answered inline: it is accepted work, not a completed result.
    [Fact]
    public async Task A_first_submission_is_acknowledged_as_accepted_work()
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(httpClient, "First Participant");
        await participant.CreateAndSelectInventoryAsync("First Warehouse");

        var response = await participant.SubmitTurnAsync("native-duplicate-3", "list stock");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("alreadyAccepted").GetBoolean());
    }

    private async Task<int> ProcessPendingAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>().ProcessPendingAsync(CancellationToken.None);
    }
}
