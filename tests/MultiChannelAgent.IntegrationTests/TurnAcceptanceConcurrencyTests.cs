using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Real SQL Server coverage (via Testcontainers) of the concurrent duplicate-Turn-acceptance race at
/// the actual HTTP application boundary: two simultaneous deliveries of the same
/// <c>nativeMessageId</c> - the exact "at-least-once redelivery arrives concurrently" shape a real
/// channel adapter can produce - must both receive <c>202 Accepted</c> and converge on one Turn
/// identity, never surface as an unhandled <c>500</c> from an unhandled
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/>. This proves the fix against the real
/// production provider end-to-end; <see cref="SqlInboxStoreConcurrencyTests"/> proves the identical
/// invariant, fast and Docker-free, directly at the repository seam.
/// </summary>
public sealed class TurnAcceptanceConcurrencyTests : SqlIntegrationTestBase
{
    private static HttpClient CreateHttpsClient(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
        });

    private static async Task<(CookieJar Jar, string CsrfToken)> SignInAndBootstrapAsync(HttpClient client)
    {
        var jar = new CookieJar();

        var signInRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/sign-in")
        {
            Content = JsonContent.Create(new { participantId = Guid.NewGuid().ToString(), displayName = "Turn Sender", activeTenantMember = true }),
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

    [SkippableFact]
    public async Task Two_concurrent_deliveries_of_the_same_native_message_id_both_receive_202_and_converge_on_one_turn()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the real SQL concurrent-acceptance scenario.");

        // Both deliveries arrive on behalf of the same signed-in Participant/browser-profile
        // conversation - the same client resubmits, mirroring real at-least-once redelivery of one
        // native message. The auth/web-conversation cookies are already established by the prior
        // sign-in/bootstrap round trip, so both requests below are built (cookies applied) before
        // either is dispatched - avoiding any race on the shared CookieJar once both run concurrently.
        var client = CreateHttpsClient(Factory!);
        var (jar, csrfToken) = await SignInAndBootstrapAsync(client);

        object Payload() => new
        {
            nativeMessageId = "native-concurrent-1",
            contentText = "hello concurrent",
            locale = "en-US",
            traceId = "trace-concurrent-1",
        };

        HttpRequestMessage BuildRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(Payload()) };
            jar.Apply(request);
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
            return request;
        }

        var requestA = BuildRequest();
        var requestB = BuildRequest();

        var taskA = client.SendAsync(requestA);
        var taskB = client.SendAsync(requestB);

        var responses = await Task.WhenAll(taskA, taskB);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        var turnIds = bodies.Select(b => b.GetProperty("turnId").GetGuid()).ToArray();
        var alreadyAcceptedFlags = bodies.Select(b => b.GetProperty("alreadyAccepted").GetBoolean()).ToArray();

        Assert.Equal(turnIds[0], turnIds[1]);
        Assert.Single(alreadyAcceptedFlags, flag => !flag);
        Assert.Single(alreadyAcceptedFlags, flag => flag);

        using var verifyScope = Factory!.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var rows = await verifyDb.InboxEntries.AsNoTracking()
            .Where(e => e.NativeMessageId == "native-concurrent-1")
            .ToListAsync();
        Assert.Single(rows);
    }
}
