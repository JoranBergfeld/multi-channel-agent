using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;
using MultiChannelAgent.Infrastructure.Persistence;
using MultiChannelAgent.Infrastructure.Persistence.Entities;
using Xunit;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// Real SQL Server coverage of the atomicity invariant behind <see cref="ITurnResultStore"/>: the
/// Outcome insert, Delivery inserts, and inbox completion update for one Turn are recorded through a
/// single <c>SaveChangesAsync</c> call, so SQL Server commits all three durable effects together or
/// none of them. This is what makes a Turn processing retry safe - before this fix, a failure between
/// separate writes could leave the Outcome recorded but the inbox entry still Pending, so a retry
/// reran model planning, created new Delivery rows, and then hit the Outcome's primary-key constraint
/// forever.
/// </summary>
public sealed class SqlTurnResultStoreTests : SqlIntegrationTestBase
{
    [SkippableFact]
    public async Task A_failed_record_attempt_leaves_no_partial_state_so_the_turn_remains_safely_retryable()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the real SQL atomicity scenario.");

        var turn = InboundTurn.Create("native-atomicity-1", "conversation-atomicity-1", "hello", null, DateTimeOffset.UtcNow, null);

        using (var seedScope = Factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            seedDb.InboxEntries.Add(new InboxEntryEntity
            {
                TurnId = turn.TurnId.Value,
                NativeMessageId = turn.NativeMessageId,
                ChannelConversationId = turn.ChannelConversationId,
                ContentText = turn.ContentText,
                ReceivedAt = turn.ReceivedAt,
                CreatedAt = turn.ReceivedAt,
                Status = InboxEntryStatus.Pending,
            });
            await seedDb.SaveChangesAsync();
        }

        var now = DateTimeOffset.UtcNow;
        var validOutcome = Outcome.Completed(turn.TurnId, "echoed", "Echoed: hello", now);
        var validDelivery = Delivery.Request(turn.TurnId, "synthetic", "Echoed: hello", now);

        // A rogue Delivery for a Turn with no InboxEntry row violates the real foreign-key
        // constraint at the database, guaranteeing SaveChangesAsync fails mid-write - after the
        // valid Outcome insert, the valid Delivery insert, and the inbox completion update were all
        // already staged in the same unit of work.
        var rogueDelivery = Delivery.Request(TurnId.NewId(), "synthetic", "orphaned", now);

        using (var attemptScope = Factory.Services.CreateScope())
        {
            var turnResultStore = attemptScope.ServiceProvider.GetRequiredService<ITurnResultStore>();
            await Assert.ThrowsAsync<DbUpdateException>(() => turnResultStore.RecordAsync(
                validOutcome,
                [validDelivery, rogueDelivery],
                CancellationToken.None));
        }

        using (var verifyScope = Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

            Assert.Null(await verifyDb.Outcomes.AsNoTracking().FirstOrDefaultAsync(o => o.TurnId == turn.TurnId.Value));
            Assert.Empty(await verifyDb.Deliveries.AsNoTracking().Where(d => d.TurnId == turn.TurnId.Value).ToListAsync());

            var inboxEntry = await verifyDb.InboxEntries.AsNoTracking().FirstAsync(e => e.TurnId == turn.TurnId.Value);
            Assert.Equal(InboxEntryStatus.Pending, inboxEntry.Status);
        }

        // A subsequent retry - the same shape a real reprocessing pass would produce after rerunning
        // model planning from scratch - now succeeds cleanly: no Outcome primary-key conflict, because
        // nothing from the failed attempt was ever durably committed.
        var retryDelivery = Delivery.Request(turn.TurnId, "synthetic", "Echoed: hello", now);
        using (var retryScope = Factory.Services.CreateScope())
        {
            var turnResultStore = retryScope.ServiceProvider.GetRequiredService<ITurnResultStore>();
            await turnResultStore.RecordAsync(validOutcome, [retryDelivery], CancellationToken.None);
        }

        using (var finalScope = Factory.Services.CreateScope())
        {
            var finalDb = finalScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

            var savedOutcome = await finalDb.Outcomes.AsNoTracking().FirstAsync(o => o.TurnId == turn.TurnId.Value);
            Assert.Equal("echoed", savedOutcome.Code);

            var savedDelivery = await finalDb.Deliveries.AsNoTracking().SingleAsync(d => d.TurnId == turn.TurnId.Value);
            Assert.Equal(DeliveryEntityStatus.Pending, savedDelivery.Status);

            var finalInboxEntry = await finalDb.InboxEntries.AsNoTracking().FirstAsync(e => e.TurnId == turn.TurnId.Value);
            Assert.Equal(InboxEntryStatus.Completed, finalInboxEntry.Status);
        }
    }
}
