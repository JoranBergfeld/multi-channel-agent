using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using Xunit;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The highest-value correctness seam for this ticket: submit a normalized synthetic InboundTurn to
/// the real HTTP application boundary, backed by an ephemeral SQL Server container with production
/// EF Core migrations applied, and assert the durable, terminal, externally observable effects
/// (Outcome + Delivery) - with no sleeps, driving processing deterministically instead.
/// </summary>
public sealed class TurnTracerScenarioTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task Submitting_a_synthetic_turn_is_durably_accepted_processed_and_produces_a_terminal_outcome_with_delivery()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the SQL-backed application-boundary scenario.");

        var client = Factory!.CreateClient();

        var submitResponse = await client.PostAsJsonAsync("/api/turns", new
        {
            nativeMessageId = "native-tracer-1",
            channelConversationId = "conversation-tracer-1",
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

        var outcomeResponse = await client.GetAsync($"/api/turns/{turnId}/outcome");
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
        // reprocess: the recorded Turn identity is returned instead.
        var duplicateResponse = await client.PostAsJsonAsync("/api/turns", new
        {
            nativeMessageId = "native-tracer-1",
            channelConversationId = "conversation-tracer-1",
            contentText = "hello tracer",
            locale = "en-US",
            traceId = "trace-tracer-1",
        });

        Assert.Equal(HttpStatusCode.Accepted, duplicateResponse.StatusCode);
        var duplicateBody = await duplicateResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(turnId, duplicateBody.GetProperty("turnId").GetGuid());
        Assert.True(duplicateBody.GetProperty("alreadyAccepted").GetBoolean());
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
