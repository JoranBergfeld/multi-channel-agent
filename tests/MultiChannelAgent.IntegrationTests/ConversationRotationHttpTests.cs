using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// "New conversation" over real HTTP, backed by SQLite (fast, Docker-free). The criterion is
/// deliberately two-sided: it must rotate model history and clear pending confirmation state, and it
/// must not remove a single thing the Participant is authorized to do - which includes the Inventory
/// they had selected, their Memberships, and a file import they had waiting in the browser.
/// </summary>
public sealed class ConversationRotationHttpTests : IAsyncLifetime
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
    public async Task Starting_a_new_conversation_rotates_the_foundry_conversation_generation()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Resetting Participant");
        await participant.CreateAndSelectInventoryAsync("Reset Warehouse");
        await participant.SubmitAcceptedTurnAsync("native-reset-1", "list stock");
        await ProcessUntilQuietAsync();

        var before = await SingleBindingAsync();

        using var response = await participant.StartNewConversationAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(before.Generation + 1, body.GetProperty("generation").GetInt32());
        Assert.NotEqual(before.FoundryConversationId.ToString(), body.GetProperty("foundryConversationId").GetString());

        var after = await SingleBindingAsync();
        Assert.Equal(before.Generation + 1, after.Generation);
        Assert.NotEqual(before.FoundryConversationId, after.FoundryConversationId);
    }

    [Fact]
    public async Task Starting_a_new_conversation_keeps_every_authorization_and_the_active_inventory()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Authorized Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Kept Warehouse");

        using (var reset = await participant.StartNewConversationAsync())
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        var bootstrap = (await participant.GetBootstrapAsync()).GetProperty("bootstrap");
        Assert.Equal(inventoryId.ToString(), bootstrap.GetProperty("activeInventoryId").GetString());
        Assert.Single(bootstrap.GetProperty("inventories").EnumerateArray());
    }

    [Fact]
    public async Task Starting_a_new_conversation_clears_the_confirmation_that_was_waiting()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Confirming Participant");
        await participant.CreateAndSelectInventoryAsync("Pending Warehouse");

        await participant.SubmitAcceptedTurnAsync("native-reset-2", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        // A batch, because a change set of more than one change always asks for confirmation whatever
        // its changes are, so this needs no particular prior Stock state. A single
        // `forget stock Steel Bolts` would instead be refused with forget_requires_zero_quantity -
        // Forget can never stand in for Remove - and would leave nothing pending to clear.
        var proposalTurn = await participant.SubmitAcceptedTurnAsync(
            "native-reset-3", "change stock: add Steel Bolts quantity 1; add Brass Rivets quantity 2");
        await ProcessUntilQuietAsync();

        var proposalOutcome = await participant.GetOutcomeAsync(proposalTurn);
        Assert.Equal("confirmation_required", proposalOutcome!.Value.GetProperty("category").GetString());
        var token = proposalOutcome.Value.GetProperty("payload").GetProperty("token").GetString()!;

        using (var response = await participant.StartNewConversationAsync())
        {
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("clearedPendingConfirmation").GetBoolean());
        }

        // The exact code they were holding no longer means anything, and saying it does not execute
        // the change they walked away from.
        var afterReset = await participant.SubmitAcceptedTurnAsync("native-reset-4", $"confirm {token}");
        await ProcessUntilQuietAsync();
        var afterOutcome = await participant.GetOutcomeAsync(afterReset);
        Assert.NotEqual("completed", afterOutcome!.Value.GetProperty("category").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var proposal = await db.ConfirmationProposals.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(ProposalStatus.ConversationReset), proposal.Status);
    }

    [Fact]
    public async Task Starting_a_new_conversation_leaves_a_waiting_file_import_exactly_where_it_was()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Importing Participant");
        var inventoryId = await participant.CreateAndSelectInventoryAsync("Importing Warehouse");

        var csv = new ByteArrayContent(Encoding.UTF8.GetBytes("Name,Quantity,Unit,Location,Note\nSteel Bolts,4,,,\n"));
        csv.Headers.ContentType = new MediaTypeHeaderValue("text/csv");

        Guid proposalId;
        using (var validate = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/import/validate")
            {
                Content = new MultipartFormDataContent { { csv, "file", "stock.csv" } },
            },
            withCsrf: true))
        {
            Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
            proposalId = (await validate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("proposalId").GetGuid();
        }

        using (var reset = await participant.StartNewConversationAsync())
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        // An Initial Import proposal is bound to a Participant and an Inventory, not to a
        // ChannelConversation: it is a browser file workflow that never became a Turn. "Clears pending
        // clarification/confirmation" means the one conversational proposal, so throwing away a file
        // the Participant already uploaded and previewed would be destroying work the reset never
        // promised to touch.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var stored = await db.ImportProposals.AsNoTracking().SingleAsync(p => p.ProposalId == proposalId);
        Assert.Equal(nameof(ImportProposalStatus.Pending), stored.Status);
    }

    [Fact]
    public async Task Starting_a_new_conversation_requires_the_csrf_token()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Forging Participant");

        using var response = await participant.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/conversation/new"), withCsrf: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Starting_a_new_conversation_requires_an_active_tenant_member()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        using var response = await http.PostAsync("/api/conversation/new", content: null);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"An unauthenticated reset must never succeed, but it returned {response.StatusCode}.");
    }

    [Fact]
    public async Task Work_accepted_before_the_reset_still_completes_in_the_conversation_it_belonged_to()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Overlapping Participant");
        await participant.CreateAndSelectInventoryAsync("Overlapping Warehouse");

        var before = await participant.SubmitAcceptedTurnAsync("native-reset-5", "list stock");
        using (var reset = await participant.StartNewConversationAsync())
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        var after = await participant.SubmitAcceptedTurnAsync("native-reset-6", "list stock");

        await ProcessUntilQuietAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var rows = await db.InboxEntries.AsNoTracking()
            .Where(e => e.TurnId == before || e.TurnId == after)
            .ToDictionaryAsync(e => e.TurnId);

        Assert.NotEqual(
            rows[before].FoundryConversationId,
            rows[after].FoundryConversationId);
        Assert.Equal(rows[before].FoundryConversationGeneration + 1, rows[after].FoundryConversationGeneration);

        // The reset changed which conversation NEW work joins. It did not abandon work already
        // accepted, and it did not break the queue that work is waiting in.
        Assert.NotNull(await participant.GetOutcomeAsync(before));
        Assert.NotNull(await participant.GetOutcomeAsync(after));
    }

    [Fact]
    public async Task A_change_proposed_by_work_from_before_the_reset_can_never_be_confirmed_after_it()
    {
        var http = ConversationTestClient.CreateHttpsClient(_factory);
        var participant = await ConversationTestClient.SignInAsync(http, "Superseded Participant");
        await participant.CreateAndSelectInventoryAsync("Superseded Warehouse");

        await participant.SubmitAcceptedTurnAsync("native-reset-7", "add stock Steel Bolts quantity 4");
        await ProcessUntilQuietAsync();

        // Accepted, then reset, then processed. The proposal this Turn asks for is created entirely
        // AFTER the reset committed, so the rotation's own transactional settle never saw it. This is
        // exactly the interleaving D10 exists for, end to end over HTTP.
        //
        // A BATCH, deliberately: StockChangeSetService confirms whenever a change set carries more
        // than one change, whatever each change is on its own, so this asks for confirmation without
        // depending on any particular prior Stock state. (A single `forget stock Steel Bolts` would
        // not: Forget refuses stock still on hand with forget_requires_zero_quantity and proposes
        // nothing, so this test would prove nothing.)
        var stale = await participant.SubmitAcceptedTurnAsync(
            "native-reset-8", "change stock: add Steel Bolts quantity 1; add Brass Rivets quantity 2");

        using (var reset = await participant.StartNewConversationAsync())
        {
            Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        }

        await ProcessUntilQuietAsync();

        // The Turn did reach the confirmation path - the proposal below exists precisely because it
        // did - but it is never offered as one: a confirmation decided in a conversation the
        // Participant has already left is answered as the conflict it now is, carrying no token,
        // because handing back a bearer secret that is already settled would only render a prompt
        // that can never succeed. There is therefore no token to try, and trying an invented one
        // would prove nothing about this Turn.
        var staleOutcome = await participant.GetOutcomeAsync(stale);
        Assert.Equal("conflict", staleOutcome!.Value.GetProperty("category").GetString());
        Assert.Equal(ConfirmationProposalLifecycle.ConversationResetCode, staleOutcome.Value.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, staleOutcome.Value.GetProperty("payload").ValueKind);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        // Nothing was changed and nothing was left confirmable, and the proposal is settled as what it
        // is: work belonging to a conversation the Participant ended. That a proposal exists at all is
        // what makes this test non-vacuous - the batch really did produce something confirmable, and
        // the reset really is what stopped it.
        Assert.Equal(
            nameof(ProposalStatus.ConversationReset),
            (await db.ConfirmationProposals.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(4m, (await db.StockEntries.AsNoTracking().SingleAsync()).Quantity);
    }

    private async Task<Domain.Turns.FoundryConversationBinding> SingleBindingAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var row = await db.FoundryConversationBindings.AsNoTracking().SingleAsync();

        return new Domain.Turns.FoundryConversationBinding
        {
            ParticipantId = new ParticipantId(row.ParticipantId),
            ChannelConversationId = new Domain.Turns.ChannelConversationId(row.ChannelConversationId),
            FoundryConversationId = new Domain.Turns.FoundryConversationId(row.FoundryConversationId),
            Generation = row.Generation,
            CreatedAt = row.CreatedAt,
        };
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
