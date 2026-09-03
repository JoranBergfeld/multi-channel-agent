using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The highest-value correctness seam for this ticket (#30): a signed-in web Participant conversing
/// "list stock" / "find &lt;name&gt;" through the real HTTP application boundary, backed by an
/// ephemeral SQL Server container with production EF Core migrations applied, proving list/find
/// through Turn acceptance -&gt; processing -&gt; Outcome -&gt; authoritative Stock projection, plus
/// native-message idempotency, per-conversation FIFO, and duplicate Outcome recovery - all with no
/// sleeps, driving processing deterministically instead.
/// </summary>
public sealed class StockConversationScenarioTests : SqlIntegrationTestBase
{
    private static HttpClient CreateHttpsClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(HttpClient client, string displayName)
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId = Guid.NewGuid().ToString(), displayName, activeTenantMember = true }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await client.SendAsync(signInRequest);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var bootstrapRequest = new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap");
        jar.Apply(bootstrapRequest);
        var bootstrapResponse = await client.SendAsync(bootstrapRequest);
        jar.Capture(bootstrapResponse);
        Assert.Equal(HttpStatusCode.OK, bootstrapResponse.StatusCode);

        var body = await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>();
        return (jar, body.GetProperty("csrfToken").GetString()!);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, CookieJar jar, HttpRequestMessage request, string? csrfToken = null)
    {
        jar.Apply(request);
        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        var response = await client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    private static async Task<Guid> CreateAndSelectInventoryAsync(HttpClient client, CookieJar jar, string csrfToken)
    {
        var createResponse = await SendAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, "/api/inventories") { Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = Guid.NewGuid().ToString() }) },
            csrfToken);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inventoryId = Guid.Parse(created.GetProperty("id").GetString()!);

        var selectResponse = await SendAsync(client, jar, new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), csrfToken);
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

        return inventoryId;
    }

    private async Task SeedStockEntryAsync(Guid inventoryId, string name, decimal quantity)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var unit = db.Units.Single(u => u.InventoryId == inventoryId);

        db.StockEntries.Add(new StockEntryEntity
        {
            Id = Guid.NewGuid(),
            InventoryId = inventoryId,
            UnitId = unit.Id,
            LocationId = null,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            Quantity = quantity,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<int> ProcessPendingAsync(CustomWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
        return await coordinator.ProcessPendingAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Listing_and_finding_stock_through_a_web_conversation_reaches_a_typed_outcome_and_the_workspace_projection_agrees()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed Stock conversation scenario.");

        var client = CreateHttpsClient(Factory!);
        var (jar, csrfToken) = await SignInAndBootstrapAsync(client, "Warehouse Owner");
        var inventoryId = await CreateAndSelectInventoryAsync(client, jar, csrfToken);
        await SeedStockEntryAsync(inventoryId, "Steel Bolts", 12m);
        await SeedStockEntryAsync(inventoryId, "Copper Wire", 0m);

        // List: defaults to On-hand Stock only.
        var listSubmit = await SendAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(new { nativeMessageId = "native-list-1", contentText = "list stock" }) },
            csrfToken);
        Assert.Equal(HttpStatusCode.Accepted, listSubmit.StatusCode);
        var listTurnId = (await listSubmit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();

        Assert.Equal(1, await ProcessPendingAsync(Factory!));

        var listOutcomeResponse = await SendAsync(client, jar, new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{listTurnId}/outcome"));
        Assert.Equal(HttpStatusCode.OK, listOutcomeResponse.StatusCode);
        var listOutcome = await listOutcomeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", listOutcome.GetProperty("status").GetString());
        var listPayload = listOutcome.GetProperty("payload");
        Assert.Equal("stock_list", listPayload.GetProperty("kind").GetString());
        var listRows = listPayload.GetProperty("rows").EnumerateArray().ToList();
        var listedRow = Assert.Single(listRows);
        Assert.Equal("Steel Bolts", listedRow.GetProperty("name").GetString());
        Assert.Equal("12", listedRow.GetProperty("quantity").GetString());

        // The authoritative workspace projection agrees with what the conversation just reported.
        var projectionResponse = await SendAsync(client, jar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/stock"));
        var projection = await projectionResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(projection.GetProperty("rows").EnumerateArray());

        // Find: an exact single match completes.
        var findSubmit = await SendAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(new { nativeMessageId = "native-find-1", contentText = "find Steel Bolts" }) },
            csrfToken);
        var findTurnId = (await findSubmit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();
        Assert.Equal(1, await ProcessPendingAsync(Factory!));

        var findOutcomeResponse = await SendAsync(client, jar, new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{findTurnId}/outcome"));
        var findOutcome = await findOutcomeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", findOutcome.GetProperty("status").GetString());
        var findPayload = findOutcome.GetProperty("payload");
        Assert.Equal("stock_find", findPayload.GetProperty("kind").GetString());
        var candidate = Assert.Single(findPayload.GetProperty("candidates").EnumerateArray());
        Assert.Equal("Steel Bolts", candidate.GetProperty("name").GetString());

        // Native-message idempotency + duplicate Outcome recovery: resubmitting the same
        // nativeMessageId is never reprocessed, and because that Turn has already been answered the
        // submission itself hands back its recorded terminal Outcome rather than an acknowledgement,
        // so a redelivering adapter never has to poll for a result the application already holds.
        var deliveriesBeforeDuplicate = await CountDeliveriesAsync(listTurnId);
        var duplicateSubmit = await SendAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(new { nativeMessageId = "native-list-1", contentText = "list stock" }) },
            csrfToken);
        Assert.Equal(HttpStatusCode.OK, duplicateSubmit.StatusCode);

        var duplicateBody = await duplicateSubmit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(listTurnId, duplicateBody.GetProperty("turnId").GetGuid());
        Assert.Equal(listOutcome.GetProperty("status").GetString(), duplicateBody.GetProperty("status").GetString());
        Assert.Equal(listOutcome.GetProperty("category").GetString(), duplicateBody.GetProperty("category").GetString());
        Assert.Equal(listOutcome.GetProperty("code").GetString(), duplicateBody.GetProperty("code").GetString());
        Assert.Equal(listOutcome.GetProperty("summary").GetString(), duplicateBody.GetProperty("summary").GetString());

        var duplicatePayload = duplicateBody.GetProperty("payload");
        Assert.Equal("stock_list", duplicatePayload.GetProperty("kind").GetString());
        Assert.Equal(
            listedRow.GetProperty("id").GetString(),
            Assert.Single(duplicatePayload.GetProperty("rows").EnumerateArray()).GetProperty("id").GetString());

        // The very same recorded response part, not a second one minted for the duplicate.
        Assert.Equal(
            listOutcome.GetProperty("deliveries")[0].GetProperty("deliveryId").GetGuid(),
            duplicateBody.GetProperty("deliveries")[0].GetProperty("deliveryId").GetGuid());

        // No pending work was created by the duplicate submission - it was never reprocessed, and no
        // further Delivery was recorded for it.
        Assert.Equal(0, await ProcessPendingAsync(Factory!));
        Assert.Equal(deliveriesBeforeDuplicate, await CountDeliveriesAsync(listTurnId));
        Assert.Equal(1, deliveriesBeforeDuplicate);
    }

    private async Task<int> CountDeliveriesAsync(Guid turnId)
    {
        using var scope = Factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        return await db.Deliveries.AsNoTracking().CountAsync(d => d.TurnId == turnId);
    }

    // Proves per-conversation FIFO end to end against real SQL Server: a Turn that cannot reach a
    // terminal Outcome leaves its own conversation's successor entirely unclaimed - never even
    // planned - across repeated passes and lease acquisitions, while an unrelated conversation keeps
    // completing; and once the fault clears the conversation resumes head-first, in order.
    // PerConversationFifoSqliteTests proves the identical behavior Docker-free.
    [SkippableFact]
    public async Task A_stuck_turn_holds_only_its_own_conversation_and_the_conversation_resumes_in_order()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed per-conversation FIFO scenario.");

        await PerConversationFifoScenario.RunAsync(Factory!);
    }
    // A conversational read must leave a durable, channel-neutral response part behind - exactly one,
    // with its own identity - and neither duplicate submission nor Delivery retries may duplicate it
    // or rerun processing. StockReadDeliverySqliteTests proves the identical behavior Docker-free.
    [SkippableFact]
    public async Task An_answered_read_records_exactly_one_channel_neutral_response_part()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed read Delivery scenario.");

        await StockReadDeliveryScenario.RunAsync(Factory!);
    }

    // Every stock mutation acceptance criterion for #31, end to end against real SQL Server with
    // production migrations applied. StockMutationSqliteTests proves the identical behavior
    // Docker-free.
    [SkippableFact]
    public async Task Adding_removing_and_setting_stock_through_a_web_conversation_behaves_exactly_as_specified()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed Stock mutation scenario.");

        await StockMutationScenario.RunAsync(Factory!);
    }
}
