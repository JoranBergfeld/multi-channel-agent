using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The highest-value correctness seam for this ticket: exercise Inventory creation, duplicate
/// (idempotent) requests, selection, and non-disclosure over the real HTTP application boundary,
/// backed by an ephemeral SQL Server container with production EF Core migrations applied - no
/// sleeps, no mocked persistence.
/// </summary>
public sealed class InventorySqlScenarioTests : SqlIntegrationTestBase
{
    private static HttpClient CreateHttpsClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(HttpClient client, string participantId, string displayName)
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId, displayName, activeTenantMember = true }),
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, CookieJar jar, HttpRequestMessage request, string csrfToken)
    {
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var response = await client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    [SkippableFact]
    public async Task Creating_duplicate_requesting_selecting_and_non_disclosure_all_hold_against_real_sql_server()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed Inventory scenario.");

        var client = CreateHttpsClient(Factory!);

        var (ownerJar, ownerCsrf) = await SignInAndBootstrapAsync(client, Guid.NewGuid().ToString(), "Owner Person");

        // 1. Creation atomically makes the requester Owner.
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
        {
            Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = "req-1" }),
        };
        var createResponse = await SendAsync(client, ownerJar, createRequest, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Owner", created.GetProperty("role").GetString());
        var inventoryId = created.GetProperty("id").GetString();

        // 2. Duplicate requests (same ClientRequestId) return the original Inventory, not a new one.
        var duplicateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
        {
            Content = JsonContent.Create(new { name = "Warehouse", clientRequestId = "req-1" }),
        };
        var duplicateResponse = await SendAsync(client, ownerJar, duplicateRequest, ownerCsrf);
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(inventoryId, duplicate.GetProperty("id").GetString());

        var listResponse = await SendAsync(client, ownerJar, new HttpRequestMessage(HttpMethod.Get, "/api/inventories"), ownerCsrf);
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(list.EnumerateArray());

        // 3. Selection: the Owner can select their own Inventory.
        var selectResponse = await SendAsync(
            client, ownerJar, new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, selectResponse.StatusCode);

        // 4. Non-disclosure: an unrelated Participant gets an empty list and a plain 404 on selection,
        // and selecting never itself grants access.
        var (outsiderJar, outsiderCsrf) = await SignInAndBootstrapAsync(client, Guid.NewGuid().ToString(), "Outsider Person");

        var outsiderListResponse = await SendAsync(client, outsiderJar, new HttpRequestMessage(HttpMethod.Get, "/api/inventories"), outsiderCsrf);
        var outsiderList = await outsiderListResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(outsiderList.EnumerateArray());

        var outsiderSelectResponse = await SendAsync(
            client, outsiderJar, new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), outsiderCsrf);
        Assert.Equal(HttpStatusCode.NotFound, outsiderSelectResponse.StatusCode);

        var outsiderListAfterResponse = await SendAsync(client, outsiderJar, new HttpRequestMessage(HttpMethod.Get, "/api/inventories"), outsiderCsrf);
        var outsiderListAfter = await outsiderListAfterResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(outsiderListAfter.EnumerateArray());
    }
}
