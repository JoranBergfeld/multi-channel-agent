using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests.Inventories;

/// <summary>
/// Real SQL Server coverage (via Testcontainers) of the concurrent Active-Inventory-selection race at
/// the actual HTTP application boundary: two simultaneous selection requests from the SAME Participant
/// and the SAME ChannelConversation - the exact "bootstrap auto-selection racing an explicit
/// multi-tab selection" or "two browser tabs selecting concurrently" shape this BFF must survive - for
/// two DIFFERENT authorized Inventories must both receive <c>200 OK</c>, never surface as an unhandled
/// <c>500</c> from an unhandled <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>, and
/// converge on exactly one Active Inventory selection row. This proves the fix against the real
/// production provider end-to-end; <see cref="SqlActiveInventorySelectionStoreConcurrencyTests"/>
/// proves the identical invariant, fast and Docker-free, directly at the repository seam.
/// </summary>
public sealed class InventorySelectionConcurrencyTests : SqlIntegrationTestBase
{
    private static HttpClient CreateHttpsClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, CookieJar jar, HttpRequestMessage request, string csrfToken)
    {
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var response = await client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    [SkippableFact]
    public async Task Two_concurrent_selections_of_different_authorized_inventories_both_receive_200_and_converge_on_one_row()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the real SQL concurrent-selection scenario.");

        var client = CreateHttpsClient(Factory!);
        var participantId = Guid.NewGuid().ToString();

        // One sign-in/bootstrap establishes the single auth cookie AND web-conversation cookie that
        // both concurrent selection requests below share - exactly like two browser tabs (or a
        // bootstrap auto-selection racing an explicit selection) for the same signed-in Participant
        // and the same conversation.
        var jar = new CookieJar();
        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId, displayName = "Race Participant", activeTenantMember = true }),
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
        var csrfToken = (await bootstrapResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;

        var createA = new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
        {
            Content = JsonContent.Create(new { name = "Warehouse A", clientRequestId = "race-a" }),
        };
        var createAResponse = await SendAsync(client, jar, createA, csrfToken);
        var inventoryAId = (await createAResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var createB = new HttpRequestMessage(HttpMethod.Post, "/api/inventories")
        {
            Content = JsonContent.Create(new { name = "Warehouse B", clientRequestId = "race-b" }),
        };
        var createBResponse = await SendAsync(client, jar, createB, csrfToken);
        var inventoryBId = (await createBResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var clientA = CreateHttpsClient(Factory!);
        var clientB = CreateHttpsClient(Factory!);

        var selectA = new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryAId}/select");
        jar.Apply(selectA);
        selectA.Headers.Add("X-CSRF-TOKEN", csrfToken);

        var selectB = new HttpRequestMessage(HttpMethod.Post, $"/api/inventories/{inventoryBId}/select");
        jar.Apply(selectB);
        selectB.Headers.Add("X-CSRF-TOKEN", csrfToken);

        // Both requests share one already-established session (same auth + web-conversation
        // cookies), applied above before dispatch to avoid mutating the shared, non-thread-safe
        // CookieJar concurrently from two in-flight responses; the selection endpoint itself sets no
        // further cookies, so no jar.Capture is needed for what this test asserts.
        var taskA = clientA.SendAsync(selectA);
        var taskB = clientB.SendAsync(selectB);

        var responses = await Task.WhenAll(taskA, taskB);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        using var verifyScope = Factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var rows = await verifyDb.ActiveInventorySelections
            .AsNoTracking()
            .Where(e => e.ParticipantId == Guid.Parse(participantId))
            .ToListAsync();

        Assert.Single(rows);
        Assert.Contains(rows.Single().InventoryId, new[] { Guid.Parse(inventoryAId), Guid.Parse(inventoryBId) });
    }
}
