using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Turns;

public sealed class TurnProgressEventCleanupCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        TurnProgressEventCleanupCoordinator Coordinator,
        InMemoryTurnProgressEventStore Progress,
        InMemoryLeaseCoordinator Leases,
        FakeTimeProvider Time);

    private static Harness CreateHarness()
    {
        var time = new FakeTimeProvider(Now);
        var progress = new InMemoryTurnProgressEventStore();
        var leases = new InMemoryLeaseCoordinator(time);

        return new Harness(
            new TurnProgressEventCleanupCoordinator(
                progress, leases, time, NullLogger<TurnProgressEventCleanupCoordinator>.Instance),
            progress,
            leases,
            time);
    }

    [Fact]
    public async Task A_pass_deletes_two_expired_progress_events()
    {
        var harness = CreateHarness();

        var expiredOne = TurnId.NewId();
        var expiredTwo = TurnId.NewId();
        var retained = TurnId.NewId();

        await harness.Progress.AppendAsync(TurnProgressEvent.Processing(expiredOne, Now), CancellationToken.None);
        await harness.Progress.AppendAsync(TurnProgressEvent.Processing(expiredTwo, Now.AddHours(-1)), CancellationToken.None);
        await harness.Progress.AppendAsync(
            TurnProgressEvent.Processing(retained, Now.Add(TurnProgressEvent.Retention).AddMinutes(-1)),
            CancellationToken.None);

        harness.Time.SetUtcNow(Now.Add(TurnProgressEvent.Retention).AddMinutes(1));

        Assert.Equal(2, await harness.Coordinator.PurgeExpiredProgressAsync(CancellationToken.None));
        Assert.Empty(await harness.Progress.ReadAsync(expiredOne, CancellationToken.None));
        Assert.Empty(await harness.Progress.ReadAsync(expiredTwo, CancellationToken.None));
        Assert.Single(await harness.Progress.ReadAsync(retained, CancellationToken.None));
    }

    [Fact]
    public async Task A_progress_event_within_retention_remains_readable()
    {
        var harness = CreateHarness();
        var turnId = TurnId.NewId();

        await harness.Progress.AppendAsync(TurnProgressEvent.Processing(turnId, Now), CancellationToken.None);

        harness.Time.SetUtcNow(Now.Add(TurnProgressEvent.Retention).Subtract(TimeSpan.FromMinutes(1)));

        Assert.Equal(0, await harness.Coordinator.PurgeExpiredProgressAsync(CancellationToken.None));
        var events = await harness.Progress.ReadAsync(turnId, CancellationToken.None);
        Assert.Single(events);
        Assert.Equal(TurnEventKind.Processing, events[0].Kind);
    }

    [Fact]
    public async Task A_pass_that_cannot_take_the_lease_does_nothing()
    {
        var harness = CreateHarness();
        var turnId = TurnId.NewId();

        await harness.Progress.AppendAsync(TurnProgressEvent.Processing(turnId, Now), CancellationToken.None);
        harness.Time.SetUtcNow(Now.AddHours(48));

        var held = await harness.Leases.TryAcquireAsync(
            "turn-progress-cleanup", "another-replica", TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(held);
        Assert.Equal(0, await harness.Coordinator.PurgeExpiredProgressAsync(CancellationToken.None));
        Assert.Single(await harness.Progress.ReadAsync(turnId, CancellationToken.None));
    }
}
