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
            LocationUniquenessKey = Guid.Empty,
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
        // nativeMessageId returns the SAME Turn identity, is never reprocessed, and reading its
        // Outcome back yields the exact same recorded terminal result.
        var duplicateSubmit = await SendAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(new { nativeMessageId = "native-list-1", contentText = "list stock" }) },
            csrfToken);
        Assert.Equal(HttpStatusCode.Accepted, duplicateSubmit.StatusCode);
        var duplicateBody = await duplicateSubmit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(listTurnId, duplicateBody.GetProperty("turnId").GetGuid());
        Assert.True(duplicateBody.GetProperty("alreadyAccepted").GetBoolean());

        // No pending work was created by the duplicate submission - it was never reprocessed.
        Assert.Equal(0, await ProcessPendingAsync(Factory!));

        var duplicateOutcomeResponse = await SendAsync(client, jar, new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{listTurnId}/outcome"));
        var duplicateOutcome = await duplicateOutcomeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(listOutcome.GetProperty("summary").GetString(), duplicateOutcome.GetProperty("summary").GetString());
        Assert.Equal(
            listOutcome.GetProperty("payload").GetProperty("rows").GetArrayLength(),
            duplicateOutcome.GetProperty("payload").GetProperty("rows").GetArrayLength());
    }

    // Proves per-conversation FIFO end to end against real SQL Server: two Turns submitted in the
    // same signed-in web conversation are both processed correctly in one claimed batch, in their
    // submission order - complementing the Application-layer unit test that proves the stronger
    // "an earlier Turn that fails to reach a terminal Outcome blocks its later same-conversation
    // successor" invariant deterministically without needing a real fault to inject over HTTP.
    [SkippableFact]
    public async Task Two_turns_in_the_same_conversation_are_both_processed_in_their_submission_order()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed per-conversation FIFO scenario.");

        var client = CreateHttpsClient(Factory!);
        var (jar, csrfToken) = await SignInAndBootstrapAsync(client, "FIFO Owner");
        var inventoryId = await CreateAndSelectInventoryAsync(client, jar, csrfToken);
        await SeedStockEntryAsync(inventoryId, "Bolts", 1m);

        var firstSubmit = await SendAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(new { nativeMessageId = "native-fifo-1", contentText = "list stock" }) },
            csrfToken);
        var firstTurnId = (await firstSubmit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();

        var secondSubmit = await SendAsync(
            client, jar,
            new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(new { nativeMessageId = "native-fifo-2", contentText = "find Bolts" }) },
            csrfToken);
        var secondTurnId = (await secondSubmit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();

        Assert.Equal(2, await ProcessPendingAsync(Factory!));

        var firstOutcomeResponse = await SendAsync(client, jar, new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{firstTurnId}/outcome"));
        var secondOutcomeResponse = await SendAsync(client, jar, new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{secondTurnId}/outcome"));
        var firstOutcome = await firstOutcomeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondOutcome = await secondOutcomeResponse.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("completed", firstOutcome.GetProperty("status").GetString());
        Assert.Equal("completed", secondOutcome.GetProperty("status").GetString());

        using var verifyScope = Factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var firstOutcomeRow = await verifyDb.Outcomes.AsNoTracking().SingleAsync(o => o.TurnId == firstTurnId);
        var secondOutcomeRow = await verifyDb.Outcomes.AsNoTracking().SingleAsync(o => o.TurnId == secondTurnId);
        Assert.True(firstOutcomeRow.CreatedAt <= secondOutcomeRow.CreatedAt);
    }
}
