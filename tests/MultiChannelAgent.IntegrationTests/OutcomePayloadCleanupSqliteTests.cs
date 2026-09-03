using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Docker-free coverage against a real relational engine that retained Outcome payloads are actually
/// discarded once they expire - the durable half of the bound, which an in-memory coordinator test
/// cannot prove: the set-based discard must translate, must touch only expired rows, and must leave
/// the Outcome itself intact so the Turn never loses its answer.
/// </summary>
public sealed class OutcomePayloadCleanupSqliteTests : IAsyncLifetime
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
    public async Task An_expired_payload_is_discarded_while_an_unexpired_one_and_the_answers_survive()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Retention Participant");
        await participant.CreateAndSelectInventoryAsync("Retention Warehouse");

        var expiredTurnId = await participant.SubmitAcceptedTurnAsync("native-retention-1", "list stock");
        var freshTurnId = await participant.SubmitAcceptedTurnAsync("native-retention-2", "list stock");
        await ProcessUntilQuietAsync();

        // Age one of the two payloads past its retention window, exactly as the passage of real time
        // would; the other stays inside it.
        await ExpirePayloadAsync(expiredTurnId);

        using (var scope = _factory.Services.CreateScope())
        {
            var purged = await scope.ServiceProvider
                .GetRequiredService<OutcomePayloadCleanupCoordinator>()
                .PurgeExpiredPayloadsAsync(CancellationToken.None);

            Assert.Equal(1, purged);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var expired = await db.Outcomes.AsNoTracking().SingleAsync(o => o.TurnId == expiredTurnId);
        var fresh = await db.Outcomes.AsNoTracking().SingleAsync(o => o.TurnId == freshTurnId);

        Assert.Null(expired.Payload);
        Assert.Null(expired.PayloadExpiresAtTicks);
        Assert.NotNull(fresh.Payload);
        Assert.NotNull(fresh.PayloadExpiresAtTicks);

        // The answers themselves are permanent, and the Turn still reads back - now without the
        // ephemeral projection it used to carry.
        Assert.Equal("completed", expired.Status.ToString().ToLowerInvariant());
        var view = await participant.GetOutcomeAsync(expiredTurnId);
        Assert.Equal("completed", view!.Value.GetProperty("status").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, view.Value.GetProperty("payload").ValueKind);
    }

    [Fact]
    public async Task An_answer_that_never_carried_a_payload_is_never_touched_by_cleanup()
    {
        var participant = await ConversationTestClient.SignInAsync(
            ConversationTestClient.CreateHttpsClient(_factory), "Echo Participant");
        var turnId = await participant.SubmitAcceptedTurnAsync("native-retention-3", "hello");
        await ProcessUntilQuietAsync();

        using var scope = _factory.Services.CreateScope();
        var purged = await scope.ServiceProvider
            .GetRequiredService<OutcomePayloadCleanupCoordinator>()
            .PurgeExpiredPayloadsAsync(CancellationToken.None);

        Assert.Equal(0, purged);
        Assert.Equal("Echoed: hello", (await participant.GetOutcomeAsync(turnId))!.Value.GetProperty("summary").GetString());
    }

    private async Task ExpirePayloadAsync(Guid turnId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
        var outcome = await db.Outcomes.SingleAsync(o => o.TurnId == turnId);
        outcome.PayloadExpiresAtTicks = DateTimeOffset.UtcNow.AddDays(-1).UtcTicks;
        await db.SaveChangesAsync();
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
