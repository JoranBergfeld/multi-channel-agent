using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
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
///
/// Also covers, against the same real SQL Server engine, the related cross-Turn
/// <see cref="Microsoft.EntityFrameworkCore.ChangeTracker"/> isolation invariant: a failed Turn's
/// record attempt must not leave stale tracked entities that contaminate a later Turn's record
/// attempt against the SAME scope/<see cref="MultiChannelAgentDbContext"/> instance - the shape
/// <see cref="Application.Turns.TurnProcessingCoordinator"/> uses when it processes a whole claimed
/// batch of Turns through one DI scope.
/// </summary>
public sealed class SqlTurnResultStoreTests : SqlIntegrationTestBase
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    [SkippableFact]
    public async Task A_failed_record_attempt_leaves_no_partial_state_so_the_turn_remains_safely_retryable()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the real SQL atomicity scenario.");

        var turn = InboundTurn.Create("native-atomicity-1", SomeParticipant, "conversation-atomicity-1", "hello", null, DateTimeOffset.UtcNow, null);

        using (var seedScope = Factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            seedDb.InboxEntries.Add(new InboxEntryEntity
            {
                TurnId = turn.TurnId.Value,
                NativeMessageId = turn.NativeMessageId,
                ParticipantId = turn.ParticipantId.Value,
                ChannelConversationId = turn.ChannelConversationId.Value,
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

    [SkippableFact]
    public async Task A_failed_record_attempt_does_not_contaminate_a_later_turns_record_attempt_in_the_same_scope()
    {
        Skip.IfNot(DockerAvailable, "Docker is not available in this environment; skipping the real SQL cross-Turn contamination scenario.");

        var turnA = InboundTurn.Create("native-contamination-a", SomeParticipant, "conversation-contamination-a", "hello a", null, DateTimeOffset.UtcNow, null);
        var turnB = InboundTurn.Create("native-contamination-b", SomeParticipant, "conversation-contamination-b", "hello b", null, DateTimeOffset.UtcNow, null);

        using (var seedScope = Factory!.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();
            foreach (var turn in new[] { turnA, turnB })
            {
                seedDb.InboxEntries.Add(new InboxEntryEntity
                {
                    TurnId = turn.TurnId.Value,
                    NativeMessageId = turn.NativeMessageId,
                    ParticipantId = turn.ParticipantId.Value,
                    ChannelConversationId = turn.ChannelConversationId.Value,
                    ContentText = turn.ContentText,
                    ReceivedAt = turn.ReceivedAt,
                    CreatedAt = turn.ReceivedAt,
                    Status = InboxEntryStatus.Pending,
                });
            }

            await seedDb.SaveChangesAsync();
        }

        var now = DateTimeOffset.UtcNow;

        // Both RecordAsync calls below share ONE scope (and therefore one MultiChannelAgentDbContext
        // instance, since it is registered scoped) - the exact production shape of
        // TurnProcessingCoordinator.ProcessPendingAsync, which processes an entire claimed batch of
        // Turns through a single scoped DbContext resolved once at the start of the pass.
        using var sharedScope = Factory.Services.CreateScope();
        var turnResultStore = sharedScope.ServiceProvider.GetRequiredService<ITurnResultStore>();

        var outcomeA = Outcome.Completed(turnA.TurnId, "echoed", "Echoed: hello a", now);
        var validDeliveryA = Delivery.Request(turnA.TurnId, "synthetic", "Echoed: hello a", now);

        // A rogue Delivery for a Turn with no InboxEntry row violates the real foreign-key constraint
        // at the database, guaranteeing SaveChangesAsync fails mid-write for Turn A - after the valid
        // Outcome insert, the valid Delivery insert, and the inbox completion update were all already
        // staged in the same unit of work.
        var rogueDelivery = Delivery.Request(TurnId.NewId(), "synthetic", "orphaned", now);

        await Assert.ThrowsAsync<DbUpdateException>(() => turnResultStore.RecordAsync(
            outcomeA,
            [validDeliveryA, rogueDelivery],
            CancellationToken.None));

        // Turn B is a completely independent, valid record attempt. Before the ChangeTracker is
        // cleared on Turn A's failure, this call fails too: EF Core resends every still-tracked
        // Added/Modified entity from the failed Turn A attempt (including the rogue Delivery) on the
        // next SaveChangesAsync in the same DbContext, so the same foreign-key violation recurs even
        // though Turn B's own data is entirely valid.
        var outcomeB = Outcome.Completed(turnB.TurnId, "echoed", "Echoed: hello b", now);
        var deliveryB = Delivery.Request(turnB.TurnId, "synthetic", "Echoed: hello b", now);

        await turnResultStore.RecordAsync(outcomeB, [deliveryB], CancellationToken.None);

        using (var verifyScope = Factory.Services.CreateScope())
        {
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

            // Turn A left no partial state: it remains exactly as it was before the failed attempt.
            Assert.Null(await verifyDb.Outcomes.AsNoTracking().FirstOrDefaultAsync(o => o.TurnId == turnA.TurnId.Value));
            Assert.Empty(await verifyDb.Deliveries.AsNoTracking().Where(d => d.TurnId == turnA.TurnId.Value).ToListAsync());
            var inboxEntryA = await verifyDb.InboxEntries.AsNoTracking().FirstAsync(e => e.TurnId == turnA.TurnId.Value);
            Assert.Equal(InboxEntryStatus.Pending, inboxEntryA.Status);

            // Turn B is fully and correctly recorded.
            var savedOutcomeB = await verifyDb.Outcomes.AsNoTracking().FirstAsync(o => o.TurnId == turnB.TurnId.Value);
            Assert.Equal("echoed", savedOutcomeB.Code);
            var savedDeliveryB = await verifyDb.Deliveries.AsNoTracking().SingleAsync(d => d.TurnId == turnB.TurnId.Value);
            Assert.Equal(DeliveryEntityStatus.Pending, savedDeliveryB.Status);
            var inboxEntryB = await verifyDb.InboxEntries.AsNoTracking().FirstAsync(e => e.TurnId == turnB.TurnId.Value);
            Assert.Equal(InboxEntryStatus.Completed, inboxEntryB.Status);
        }
    }
}
