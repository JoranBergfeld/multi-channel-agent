using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Infrastructure.Persistence;
using Xunit;

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
    [SkippableFact]
    public async Task Two_concurrent_deliveries_of_the_same_native_message_id_both_receive_202_and_converge_on_one_turn()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the real SQL concurrent-acceptance scenario.");

        var clientA = Factory!.CreateClient();
        var clientB = Factory.CreateClient();

        object Payload() => new
        {
            nativeMessageId = "native-concurrent-1",
            channelConversationId = "conversation-concurrent-1",
            contentText = "hello concurrent",
            locale = "en-US",
            traceId = "trace-concurrent-1",
        };

        var taskA = clientA.PostAsJsonAsync("/api/turns", Payload());
        var taskB = clientB.PostAsJsonAsync("/api/turns", Payload());

        var responses = await Task.WhenAll(taskA, taskB);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<JsonElement>()));
        var turnIds = bodies.Select(b => b.GetProperty("turnId").GetGuid()).ToArray();
        var alreadyAcceptedFlags = bodies.Select(b => b.GetProperty("alreadyAccepted").GetBoolean()).ToArray();

        Assert.Equal(turnIds[0], turnIds[1]);
        Assert.Single(alreadyAcceptedFlags, flag => !flag);
        Assert.Single(alreadyAcceptedFlags, flag => flag);

        using var verifyScope = Factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var rows = await verifyDb.InboxEntries.AsNoTracking()
            .Where(e => e.NativeMessageId == "native-concurrent-1")
            .ToListAsync();
        Assert.Single(rows);
    }
}
