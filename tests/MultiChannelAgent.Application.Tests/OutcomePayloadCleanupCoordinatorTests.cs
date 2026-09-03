using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class OutcomePayloadCleanupCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (OutcomePayloadCleanupCoordinator Coordinator, InMemoryOutcomeStore Outcomes, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider(Now);
        var outcomes = new InMemoryOutcomeStore();
        var coordinator = new OutcomePayloadCleanupCoordinator(
            outcomes, new InMemoryLeaseCoordinator(time), time, NullLogger<OutcomePayloadCleanupCoordinator>.Instance);

        return (coordinator, outcomes, time);
    }

    [Fact]
    public async Task A_payload_is_retained_until_its_expiry_has_passed()
    {
        var (coordinator, outcomes, time) = Create();
        var turnId = TurnId.NewId();
        await outcomes.SaveAsync(Outcome.Completed(turnId, "completed", "1 Stock Entry found.", Now, """{"kind":"stock_list"}"""), CancellationToken.None);

        time.SetUtcNow(Now + Outcome.PayloadRetention - TimeSpan.FromMinutes(1));
        var purgedCount = await coordinator.PurgeExpiredPayloadsAsync(CancellationToken.None);

        Assert.Equal(0, purgedCount);
        Assert.NotNull((await outcomes.FindAsync(turnId, CancellationToken.None))!.Payload);
    }

    // Cleanup drops the ephemeral projection only: the Turn keeps its answer forever.
    [Fact]
    public async Task An_expired_payload_is_discarded_while_its_semantic_answer_survives()
    {
        var (coordinator, outcomes, time) = Create();
        var turnId = TurnId.NewId();
        await outcomes.SaveAsync(
            Outcome.Record(turnId, OutcomeCategory.Ambiguous, "ambiguous", "2 Stock Entries match.", Now, """{"kind":"stock_find"}"""),
            CancellationToken.None);

        time.SetUtcNow(Now + Outcome.PayloadRetention + TimeSpan.FromMinutes(1));
        var purgedCount = await coordinator.PurgeExpiredPayloadsAsync(CancellationToken.None);

        Assert.Equal(1, purgedCount);
        var outcome = await outcomes.FindAsync(turnId, CancellationToken.None);
        Assert.Null(outcome!.Payload);
        Assert.Null(outcome.PayloadExpiresAt);
        Assert.Equal(OutcomeCategory.Ambiguous, outcome.Category);
        Assert.Equal("2 Stock Entries match.", outcome.Summary);
    }

    [Fact]
    public async Task An_outcome_that_never_carried_a_payload_is_untouched()
    {
        var (coordinator, outcomes, time) = Create();
        var turnId = TurnId.NewId();
        await outcomes.SaveAsync(Outcome.Completed(turnId, "echoed", "Echoed: hello", Now), CancellationToken.None);

        time.SetUtcNow(Now + TimeSpan.FromDays(365));

        Assert.Equal(0, await coordinator.PurgeExpiredPayloadsAsync(CancellationToken.None));
        Assert.Equal("Echoed: hello", (await outcomes.FindAsync(turnId, CancellationToken.None))!.Summary);
    }

    [Fact]
    public async Task Cleanup_runs_only_under_its_own_exclusive_lease()
    {
        var time = new FakeTimeProvider(Now);
        var leases = new InMemoryLeaseCoordinator(time);
        var outcomes = new InMemoryOutcomeStore();
        await outcomes.SaveAsync(Outcome.Completed(TurnId.NewId(), "completed", "found", Now, """{"kind":"stock_list"}"""), CancellationToken.None);
        var coordinator = new OutcomePayloadCleanupCoordinator(outcomes, leases, time, NullLogger<OutcomePayloadCleanupCoordinator>.Instance);

        time.SetUtcNow(Now + Outcome.PayloadRetention + TimeSpan.FromMinutes(1));

        // Another replica already holds the lease, so this pass must do nothing at all - even though
        // there is now an expired payload it would otherwise discard.
        await leases.TryAcquireAsync("outcome-payload-cleanup", "other-replica", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(0, await coordinator.PurgeExpiredPayloadsAsync(CancellationToken.None));
    }
}
