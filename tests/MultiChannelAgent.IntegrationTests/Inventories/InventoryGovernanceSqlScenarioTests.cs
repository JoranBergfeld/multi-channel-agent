using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// The highest-value correctness seam for this ticket's governance surface: exercise membership
/// grant, ownership transfer, and orphan recovery over the real HTTP application boundary, backed by
/// an ephemeral SQL Server container with production EF Core migrations applied - no sleeps, no
/// mocked persistence. The SQLite-backed, Docker-free equivalent (broader scenario coverage) lives in
/// <see cref="InventoryGovernanceHttpTests"/> and <see cref="InventoryRecoveryHttpTests"/>.
/// </summary>
public sealed class InventoryGovernanceSqlScenarioTests : SqlIntegrationTestBase
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

    private static async Task<(CookieJar Jar, string CsrfToken)> SignInAsRecoveryAdministratorAsync(HttpClient client, string displayName)
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new
            {
                participantId = Guid.NewGuid().ToString(),
                displayName,
                activeTenantMember = false,
                isInventoryRecoveryAdministrator = true,
            }),
        };
        jar.Apply(signInRequest);
        var signInResponse = await client.SendAsync(signInRequest);
        jar.Capture(signInResponse);
        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/recovery/orphaned-inventories");
        jar.Apply(listRequest);
        var listResponse = await client.SendAsync(listRequest);
        jar.Capture(listResponse);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var body = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
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
    public async Task Grant_transfer_and_orphan_recovery_all_hold_atomically_against_real_sql_server()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed governance scenario.");

        var client = CreateHttpsClient(Factory!);

        var ownerId = Guid.NewGuid().ToString();
        var viewerId = Guid.NewGuid().ToString();
        var recoveredOwnerId = Guid.NewGuid().ToString();

        var (ownerJar, ownerCsrf) = await SignInAndBootstrapAsync(client, ownerId, "Owner Person");
        var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
        {
            Content = JsonContent.Create(new { name = "Governed Warehouse", clientRequestId = "req-governance-1" }),
        };
        var createResponse = await SendAsync(client, ownerJar, createRequest, ownerCsrf);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var inventoryId = created.GetProperty("id").GetString()!;

        // 1. Grant Viewer to a resolvable active tenant member - no acceptance step required.
        var (viewerJar, viewerCsrf) = await SignInAndBootstrapAsync(client, viewerId, "Viewer Person");

        var grantRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/inventories/{inventoryId}/members")
        {
            Content = JsonContent.Create(new { targetIdentifier = viewerId, role = "Viewer" }),
        };
        var grantResponse = await SendAsync(client, ownerJar, grantRequest, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        var viewerSelectResponse = await SendAsync(
            client, viewerJar, new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/select"), viewerCsrf);
        Assert.Equal(HttpStatusCode.OK, viewerSelectResponse.StatusCode);

        // 2. Transfer ownership atomically to the Viewer; the previous Owner is demoted to Editor,
        // and exactly one Owner exists afterward.
        var transferRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryId}/transfer-ownership")
        {
            Content = JsonContent.Create(new { targetIdentifier = viewerId }),
        };
        var transferResponse = await SendAsync(client, ownerJar, transferRequest, ownerCsrf);
        Assert.Equal(HttpStatusCode.OK, transferResponse.StatusCode);

        var membersAfterTransfer = await (await SendAsync(
            client, viewerJar, new HttpRequestMessage(HttpMethod.Get, $"/api/inventories/{inventoryId}/members"), viewerCsrf))
            .Content.ReadFromJsonAsync<JsonElement>();
        var rolesAfterTransfer = membersAfterTransfer.EnumerateArray()
            .ToDictionary(m => m.GetProperty("participantId").GetString()!, m => m.GetProperty("role").GetString()!);
        Assert.Equal("Owner", rolesAfterTransfer[viewerId]);
        Assert.Equal("Editor", rolesAfterTransfer[ownerId]);
        Assert.Single(rolesAfterTransfer.Values, r => r == "Owner");

        // 3. Orphan recovery: the new Owner (formerly Viewer) leaves the tenant; a Recovery
        // Administrator identifies the orphaned Inventory and transfers ownership without ever
        // becoming a member or touching stock.
        var unregisterRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/tenant-directory/unregister")
        {
            Content = JsonContent.Create(new { participantId = viewerId, displayName = "unused" }),
        };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(unregisterRequest)).StatusCode);

        var (recoveredJar, recoveredCsrf) = await SignInAndBootstrapAsync(client, recoveredOwnerId, "Recovered Owner");
        var (adminJar, adminCsrf) = await SignInAsRecoveryAdministratorAsync(client, "Recovery Admin");

        var listResponse = await SendAsync(client, adminJar, new HttpRequestMessage(HttpMethod.Get, "/api/recovery/orphaned-inventories"), adminCsrf);
        var page = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var orphanedIds = page.GetProperty("page").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("inventoryId").GetString())
            .ToList();
        Assert.Contains(inventoryId, orphanedIds);

        var recoverRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/recovery/inventories/{inventoryId}/recover")
        {
            Content = JsonContent.Create(new { targetIdentifier = recoveredOwnerId }),
        };
        var recoverResponse = await SendAsync(client, adminJar, recoverRequest, adminCsrf);
        Assert.Equal(HttpStatusCode.OK, recoverResponse.StatusCode);

        var recoveredBootstrap = await SendAsync(client, recoveredJar, new HttpRequestMessage(HttpMethod.Get, "/api/session/bootstrap"), recoveredCsrf);
        var recoveredBody = await recoveredBootstrap.Content.ReadFromJsonAsync<JsonElement>();
        var owned = recoveredBody.GetProperty("bootstrap").GetProperty("inventories").EnumerateArray()
            .Single(i => i.GetProperty("id").GetString() == inventoryId);
        Assert.Equal("Owner", owned.GetProperty("role").GetString());
    }
}
