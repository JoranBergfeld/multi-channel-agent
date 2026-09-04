using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests;

public class TurnProcessingCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    /// <summary>Counts invocations so tests can assert exactly when model planning does and does not rerun.</summary>
    private sealed class CountingModelBoundary(IModelBoundary inner) : IModelBoundary
    {
        public int InvocationCount { get; private set; }

        public Task<ModelProposal> ProposeAsync(InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken)
        {
            InvocationCount++;
            return inner.ProposeAsync(turn, context, cancellationToken);
        }
    }

    /// <summary>Captures which trusted Foundry conversation each Turn's model planning was given.</summary>
    private sealed class CapturingModelBoundary(IModelBoundary inner) : IModelBoundary
    {
        public List<(ChannelConversationId Conversation, FoundryConversationId Foundry)> Invocations { get; } = [];

        public Task<ModelProposal> ProposeAsync(InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken)
        {
            Invocations.Add((turn.ChannelConversationId, context.FoundryConversationId));
            return inner.ProposeAsync(turn, context, cancellationToken);
        }
    }

    private sealed class ProgressObservingModelBoundary(
        IModelBoundary inner,
        InMemoryTurnProgressEventStore progressStore) : IModelBoundary
    {
        public Task<ModelProposal> ProposeAsync(
            InboundTurn turn,
            ModelInvocationContext context,
            CancellationToken cancellationToken)
        {
            progressStore.ModelWasCalled = true;
            return inner.ProposeAsync(turn, context, cancellationToken);
        }
    }

    private static (TurnProcessingCoordinator Coordinator, InMemoryInboxStore Inbox, InMemoryOutcomeStore Outcomes, InMemoryDeliveryStore Deliveries, InMemoryTurnResultStore ResultStore, InMemoryFoundryConversationBindingStore Bindings)
        CreateCoordinator(
            TimeProvider timeProvider,
            IModelBoundary? modelBoundary = null,
            InMemoryTurnProgressEventStore? progressStore = null)
    {
        var inbox = new InMemoryInboxStore();
        var outcomes = new InMemoryOutcomeStore();
        var deliveries = new InMemoryDeliveryStore();
        var resultStore = new InMemoryTurnResultStore(inbox, outcomes, deliveries);
        progressStore ??= new InMemoryTurnProgressEventStore();
        var leases = new InMemoryLeaseCoordinator(timeProvider);

        // These tests never exercise the tool-dispatch path (their content is always "hello" or the
        // scripted failure marker, both Direct decisions) - real, minimally wired instances are used
        // here purely to satisfy the constructor, matching the pattern of Application services
        // elsewhere backed by in-memory stores.
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        var selectionStore = new InMemoryActiveInventorySelectionStore();
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(selectionStore);
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var selectionService = new InventorySelectionService(authorizationService, selectionStore, new InMemoryConfirmationProposalStore());
        var bindingStore = new InMemoryFoundryConversationBindingStore();
        var executionContextFactory = new TurnExecutionContextFactory(bindingStore, selectionService);
        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
        var proposalStore = new InMemoryConfirmationProposalStore();
        var changeSetStore = new InMemoryStockChangeSetStore(stockStore, proposalStore);
        var toolDispatcher = new StockToolDispatcher(
            new StockListingService(stockStore, referenceStore, authorizationService),
            new StockFindingService(stockStore, referenceStore, authorizationService),
            new StockMutationService(
                stockStore, new InMemoryStockMutationStore(stockStore), referenceStore, authorizationService),
            new StockChangeSetService(
                new StockChangeResolver(stockStore, referenceStore), changeSetStore, proposalStore, authorizationService),
            new InventoryConfirmationService(
                proposalStore, changeSetStore, new InMemoryReferenceAdministrationStore(proposalStore), authorizationService));

        var coordinator = new TurnProcessingCoordinator(
            inbox,
            resultStore,
            progressStore,
            leases,
            new ProgressObservingModelBoundary(modelBoundary ?? new ScriptedModelBoundary(), progressStore),
            executionContextFactory,
            new ConfirmationProposalLifecycle(new InMemoryConfirmationProposalStore()),
            toolDispatcher,
            timeProvider,
            NullLogger<TurnProcessingCoordinator>.Instance);

        return (coordinator, inbox, outcomes, deliveries, resultStore, bindingStore);
    }

    [Fact]
    public async Task Processing_a_pending_turn_records_a_terminal_outcome_and_a_requested_delivery()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, outcomes, deliveries, _, _) = CreateCoordinator(timeProvider);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
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
    public async Task Processing_publishes_one_fixed_processing_event_before_the_first_model_call()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var progressStore = new InMemoryTurnProgressEventStore();
        var (coordinator, inbox, _, _, _, _) = CreateCoordinator(timeProvider, progressStore: progressStore);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);

        var progressEvent = Assert.Single(await progressStore.ReadAsync(turn.TurnId, CancellationToken.None));
        Assert.Equal(TurnEventSequence.Processing, progressEvent.Sequence);
        Assert.Equal(TurnEventKind.Processing, progressEvent.Kind);
        Assert.Equal(Now, progressEvent.OccurredAt);
        Assert.True(progressStore.ModelWasCalled);
        Assert.True(progressStore.WasAppendedBeforeFirstModelCall);
    }

    [Fact]
    public async Task Retrying_after_terminal_recording_fails_keeps_one_processing_event()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var progressStore = new InMemoryTurnProgressEventStore();
        var (coordinator, inbox, _, _, resultStore, _) = CreateCoordinator(timeProvider, progressStore: progressStore);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);
        resultStore.FailNextRecord = true;

        Assert.Equal(0, await coordinator.ProcessPendingAsync(CancellationToken.None));
        Assert.Equal(1, await coordinator.ProcessPendingAsync(CancellationToken.None));

        var progressEvent = Assert.Single(await progressStore.ReadAsync(turn.TurnId, CancellationToken.None));
        Assert.Equal(TurnEventSequence.Processing, progressEvent.Sequence);
    }

    [Fact]
    public async Task Processing_marks_the_inbox_entry_completed_so_it_is_not_claimed_again()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, _, _, _, _) = CreateCoordinator(timeProvider);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);
        var secondPassCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, secondPassCount);
    }

    [Fact]
    public async Task With_no_pending_turns_processing_reports_zero_without_error()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, _, _, _, _, _) = CreateCoordinator(timeProvider);

        var processedCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(0, processedCount);
    }

    [Fact]
    public async Task Scripted_failure_marker_records_a_failed_outcome_with_no_delivery()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, outcomes, deliveries, _, _) = CreateCoordinator(timeProvider);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", ScriptedModelBoundary.FailureMarker, null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);

        var outcome = await outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.Equal(OutcomeStatus.Failed, outcome!.Status);
        Assert.Empty(deliveries.Deliveries);
    }

    [Fact]
    public async Task A_turn_whose_result_fails_to_record_does_not_prevent_later_pending_turns_in_the_same_batch_from_processing()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, outcomes, _, resultStore, _) = CreateCoordinator(timeProvider);
        var failingTurn = TestTurns.Text("native-fail", SomeParticipant, "conversation-1", "hello", null, Now, null);
        var okTurn = TestTurns.Text("native-ok", SomeParticipant, "conversation-2", "hello", null, Now, null);
        await inbox.AcceptAsync(failingTurn, CancellationToken.None);
        await inbox.AcceptAsync(okTurn, CancellationToken.None);
        resultStore.FailForTurnIds.Add(failingTurn.TurnId.Value);

        var processedCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(1, processedCount);
        Assert.Null(await outcomes.FindAsync(failingTurn.TurnId, CancellationToken.None));
        Assert.NotNull(await outcomes.FindAsync(okTurn.TurnId, CancellationToken.None));
    }

    // Per-conversation FIFO: an earlier Turn in a ChannelConversation that does not reach a terminal
    // Outcome this pass must never let a later Turn in that SAME ChannelConversation be processed
    // ahead of it in the same batch - that would let the later Turn's Outcome/Delivery land before
    // the earlier one's, violating the ordering guarantee a Participant's conversation depends on.
    [Fact]
    public async Task A_failing_turn_blocks_only_later_turns_in_its_own_channel_conversation_this_pass()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, outcomes, _, resultStore, _) = CreateCoordinator(timeProvider);
        var firstInConversation = TestTurns.Text("native-first", SomeParticipant, "conversation-1", "hello", null, Now, null);
        var secondInSameConversation = TestTurns.Text(
            "native-second", SomeParticipant, "conversation-1", "hello", null, Now.AddSeconds(1), null);
        var turnInOtherConversation = TestTurns.Text(
            "native-other", SomeParticipant, "conversation-2", "hello", null, Now.AddSeconds(2), null);
        await inbox.AcceptAsync(firstInConversation, CancellationToken.None);
        await inbox.AcceptAsync(secondInSameConversation, CancellationToken.None);
        await inbox.AcceptAsync(turnInOtherConversation, CancellationToken.None);
        resultStore.FailForTurnIds.Add(firstInConversation.TurnId.Value);

        var processedCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        // Only the unrelated conversation's Turn is processed this pass; the same-conversation
        // successor is left pending rather than skipping ahead of its still-pending predecessor.
        Assert.Equal(1, processedCount);
        Assert.Null(await outcomes.FindAsync(firstInConversation.TurnId, CancellationToken.None));
        Assert.Null(await outcomes.FindAsync(secondInSameConversation.TurnId, CancellationToken.None));
        Assert.NotNull(await outcomes.FindAsync(turnInOtherConversation.TurnId, CancellationToken.None));

        // Once the predecessor is resolved (here: the fault stops repeating), a later pass processes
        // the successor - the block is per-pass, not a permanent poison of the conversation.
        resultStore.FailForTurnIds.Remove(firstInConversation.TurnId.Value);
        var secondPassCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(2, secondPassCount);
        Assert.NotNull(await outcomes.FindAsync(firstInConversation.TurnId, CancellationToken.None));
        Assert.NotNull(await outcomes.FindAsync(secondInSameConversation.TurnId, CancellationToken.None));
    }

    [Fact]
    public async Task Retrying_after_a_failed_result_write_reruns_model_planning_but_never_reruns_it_once_the_turn_has_completed()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var modelBoundary = new CountingModelBoundary(new ScriptedModelBoundary());
        var (coordinator, inbox, outcomes, _, resultStore, _) = CreateCoordinator(timeProvider, modelBoundary);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);
        resultStore.FailForTurnIds.Add(turn.TurnId.Value);

        // First pass: the atomic result write fails, so nothing (Outcome, Delivery, or inbox
        // completion) is recorded - the Turn remains exactly as pending as before the attempt.
        var firstPassCount = await coordinator.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(0, firstPassCount);
        Assert.Equal(1, modelBoundary.InvocationCount);
        Assert.Null(await outcomes.FindAsync(turn.TurnId, CancellationToken.None));

        // Second pass (the fault is now resolved): since nothing durable was recorded, it is safe -
        // and necessary - to rerun model planning and then record the result once, without any
        // Outcome primary-key conflict.
        resultStore.FailForTurnIds.Remove(turn.TurnId.Value);
        var secondPassCount = await coordinator.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(1, secondPassCount);
        Assert.Equal(2, modelBoundary.InvocationCount);
        Assert.NotNull(await outcomes.FindAsync(turn.TurnId, CancellationToken.None));

        // Third pass: the Turn is now completed, so it is never reclaimed and model planning never
        // reruns for it again.
        var thirdPassCount = await coordinator.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(0, thirdPassCount);
        Assert.Equal(2, modelBoundary.InvocationCount);
    }
    // Every processed Turn belongs to a Foundry conversation - including one the model answers
    // directly, with no tool call. Establishing the binding only on the tool path would leave a
    // conversation's history split across generations depending on what its Turns happened to ask
    // for, and would deny the model boundary the trusted conversation it must continue.
    [Fact]
    public async Task A_directly_answered_turn_still_establishes_its_foundry_conversation_binding()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var (coordinator, inbox, _, _, _, bindings) = CreateCoordinator(timeProvider);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);

        var binding = Assert.Single(bindings.Bindings);
        Assert.Equal(SomeParticipant, binding.ParticipantId);
        Assert.Equal(turn.ChannelConversationId, binding.ChannelConversationId);
    }

    [Fact]
    public async Task Model_planning_is_given_the_trusted_foundry_conversation_stable_per_participant_and_conversation()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var capturing = new CapturingModelBoundary(new ScriptedModelBoundary());
        var (coordinator, inbox, _, _, _, bindings) = CreateCoordinator(timeProvider, capturing);
        await inbox.AcceptAsync(
            TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null), CancellationToken.None);
        await inbox.AcceptAsync(
            TestTurns.Text("native-2", SomeParticipant, "conversation-1", "hello again", null, Now, null), CancellationToken.None);
        await inbox.AcceptAsync(
            TestTurns.Text("native-3", SomeParticipant, "conversation-2", "hello there", null, Now, null), CancellationToken.None);

        await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(3, capturing.Invocations.Count);
        Assert.All(capturing.Invocations, invocation => Assert.NotEqual(default, invocation.Foundry.Value));

        var byConversation = capturing.Invocations
            .GroupBy(invocation => invocation.Conversation)
            .ToDictionary(group => group.Key, group => group.Select(invocation => invocation.Foundry).Distinct().ToList());

        // Both Turns of one ChannelConversation continue the very same Foundry conversation, and a
        // different ChannelConversation never shares it.
        Assert.Single(byConversation[new ChannelConversationId("conversation-1")]);
        Assert.Single(byConversation[new ChannelConversationId("conversation-2")]);
        Assert.NotEqual(
            byConversation[new ChannelConversationId("conversation-1")][0],
            byConversation[new ChannelConversationId("conversation-2")][0]);
        Assert.Equal(2, bindings.Bindings.Count);
    }
}
