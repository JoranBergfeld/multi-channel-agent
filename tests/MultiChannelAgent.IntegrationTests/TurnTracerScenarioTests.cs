using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.IntegrationTests.Inventories;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The highest-value correctness seam for this ticket: submit a normalized synthetic InboundTurn to
/// the real HTTP application boundary, backed by an ephemeral SQL Server container with production
/// EF Core migrations applied, and assert the durable, terminal, externally observable effects
/// (Outcome + Delivery) - with no sleeps, driving processing deterministically instead. Turn
/// submission requires the same signed-in, CSRF-protected shape as every other mutating request:
/// Participant and ChannelConversation are always derived from trusted context, never client input.
/// </summary>
public sealed class TurnTracerScenarioTests : SqlIntegrationTestBase
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

    private static async Task<HttpResponseMessage> PostTurnAsync(HttpClient client, CookieJar jar, string csrfToken, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/turns") { Content = JsonContent.Create(payload) };
        jar.Apply(request);
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        var response = await client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    private static async Task<HttpResponseMessage> GetOutcomeAsync(HttpClient client, CookieJar jar, Guid turnId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/turns/{turnId}/outcome");
        jar.Apply(request);
        var response = await client.SendAsync(request);
        jar.Capture(response);
        return response;
    }

    [SkippableFact]
    public async Task Submitting_a_synthetic_turn_is_durably_accepted_processed_and_produces_a_terminal_outcome_with_delivery()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed application-boundary scenario.");

        var client = CreateHttpsClient(Factory!);
        var (jar, csrfToken) = await SignInAndBootstrapAsync(client);

        var submitResponse = await PostTurnAsync(client, jar, csrfToken, new
        {
            nativeMessageId = "native-tracer-1",
            contentText = "hello tracer",
            locale = "en-US",
            traceId = "trace-tracer-1",
        });

        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        var submitBody = await submitResponse.Content.ReadFromJsonAsync<JsonElement>();
        var turnId = submitBody.GetProperty("turnId").GetGuid();
        Assert.False(submitBody.GetProperty("alreadyAccepted").GetBoolean());

        // Drive the hosted-worker duties deterministically via their internal one-shot operations,
        // rather than timing the periodic background loop.
        using (var scope = Factory!.Services.CreateScope())
        {
            var processingCoordinator = scope.ServiceProvider.GetRequiredService<TurnProcessingCoordinator>();
            var processedCount = await processingCoordinator.ProcessPendingAsync(CancellationToken.None);
            Assert.Equal(1, processedCount);

            var dispatchCoordinator = scope.ServiceProvider.GetRequiredService<DeliveryDispatchCoordinator>();
            var dispatchedCount = await dispatchCoordinator.DispatchPendingAsync(CancellationToken.None);
            Assert.Equal(1, dispatchedCount);
        }

        var outcomeResponse = await GetOutcomeAsync(client, jar, turnId);
        Assert.Equal(HttpStatusCode.OK, outcomeResponse.StatusCode);
        var outcome = await outcomeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", outcome.GetProperty("status").GetString());
        Assert.Equal("echoed", outcome.GetProperty("code").GetString());
        Assert.Equal("Echoed: hello tracer", outcome.GetProperty("summary").GetString());

        var deliveries = outcome.GetProperty("deliveries").EnumerateArray().ToList();
        var delivery = Assert.Single(deliveries);
        Assert.Equal("synthetic", delivery.GetProperty("channel").GetString());
        Assert.Equal("delivered", delivery.GetProperty("status").GetString());
        Assert.Equal(1, delivery.GetProperty("attempts").GetInt32());

        // At-least-once redelivery of the same native message must not duplicate acceptance or
        // reprocess: the recorded Turn identity is returned instead, and its already-recorded terminal
        // Outcome is what the caller reads back - never a fresh reprocessing.
        var duplicateResponse = await PostTurnAsync(client, jar, csrfToken, new
        {
            nativeMessageId = "native-tracer-1",
            contentText = "hello tracer",
            locale = "en-US",
            traceId = "trace-tracer-1",
        });

        Assert.Equal(HttpStatusCode.Accepted, duplicateResponse.StatusCode);
        var duplicateBody = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turnId, duplicateBody.GetProperty("turnId").GetGuid());
        Assert.True(duplicateBody.GetProperty("alreadyAccepted").GetBoolean());

        var duplicateOutcomeResponse = await GetOutcomeAsync(client, jar, turnId);
        var duplicateOutcome = await duplicateOutcomeResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("completed", duplicateOutcome.GetProperty("status").GetString());
        var duplicateDelivery = Assert.Single(duplicateOutcome.GetProperty("deliveries").EnumerateArray());
        Assert.Equal(1, duplicateDelivery.GetProperty("attempts").GetInt32());
    }

    // A Participant may only ever read their own Turn's recorded Outcome: reading a Turn belonging to
    // a different Participant must be a plain 404, identical to an unknown Turn id, never a distinct
    // signal that would let a caller infer another Participant's Turn exists.
    [SkippableFact]
    public async Task Reading_another_participants_turn_outcome_is_a_plain_404()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed application-boundary scenario.");

        var client = CreateHttpsClient(Factory!);
        var (ownerJar, ownerCsrf) = await SignInAndBootstrapAsync(client);

        var submitResponse = await PostTurnAsync(client, ownerJar, ownerCsrf, new
        {
            nativeMessageId = "native-ownership-1",
            contentText = "hello ownership",
        });
        var turnId = (await submitResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetGuid();

        var (strangerJar, _) = await SignInAndBootstrapAsync(client);
        var strangerResponse = await GetOutcomeAsync(client, strangerJar, turnId);

        Assert.Equal(HttpStatusCode.NotFound, strangerResponse.StatusCode);
    }

    [SkippableFact]
    public async Task Health_endpoints_report_healthy_once_the_database_is_reachable()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping health check verification against real SQL Server.");

        var client = Factory!.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
