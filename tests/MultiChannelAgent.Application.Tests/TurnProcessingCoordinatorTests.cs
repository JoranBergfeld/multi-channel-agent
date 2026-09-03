using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class TurnProcessingCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (TurnProcessingCoordinator Coordinator, InMemoryInboxStore Inbox, InMemoryOutcomeStore Outcomes, InMemoryDeliveryStore Deliveries)
        CreateCoordinator(TimeProvider timeProvider, IModelBoundary? modelBoundary = null)
    {
        var inbox = new InMemoryInboxStore();
        var outcomes = new InMemoryOutcomeStore();
        var deliveries = new InMemoryDeliveryStore();
        var leases = new InMemoryLeaseCoordinator(timeProvider);
        var coordinator = new TurnProcessingCoordinator(
            inbox,
            outcomes,
            deliveries,
            leases,
            modelBoundary ?? new ScriptedModelBoundary(),
            timeProvider);

        return (coordinator, inbox, outcomes, deliveries);
    }

    [Fact]
    public async Task Processing_a_pending_turn_records_a_terminal_outcome_and_a_requested_delivery()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, outcomes, deliveries) = CreateCoordinator(timeProvider);
        var turn = InboundTurn.Create("native-1", "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        var processedCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, processedCount);
        var outcome = await outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.Equal(OutcomeStatus.Completed, outcome!.Status);
        Assert.Equal("Echoed: hello", outcome.Summary);
        var delivery = Assert.Single(deliveries.Deliveries);
        Assert.Equal(turn.TurnId, delivery.TurnId);
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
    }

    [Fact]
    public async Task Processing_marks_the_inbox_entry_completed_so_it_is_not_claimed_again()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, _, _) = CreateCoordinator(timeProvider);
        var turn = InboundTurn.Create("native-1", "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);
        var secondPassCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, secondPassCount);
    }

    [Fact]
    public async Task With_no_pending_turns_processing_reports_zero_without_error()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, _, _, _) = CreateCoordinator(timeProvider);

        var processedCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, processedCount);
    }

    [Fact]
    public async Task Scripted_failure_marker_records_a_failed_outcome_with_no_delivery()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, outcomes, deliveries) = CreateCoordinator(timeProvider);
        var turn = InboundTurn.Create("native-1", "conversation-1", ScriptedModelBoundary.FailureMarker, null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);

        var outcome = await outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.Equal(OutcomeStatus.Failed, outcome!.Status);
        Assert.Empty(deliveries.Deliveries);
    }
}
