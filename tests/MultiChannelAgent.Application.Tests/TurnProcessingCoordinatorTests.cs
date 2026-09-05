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

    private sealed class CancelingProgressEventStore : ITurnProgressEventStore
    {
        public Task<bool> AppendAsync(TurnProgressEvent progressEvent, CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);

        public Task<IReadOnlyList<TurnProgressEvent>> ReadAsync(TurnId turnId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteExpiredAsync(DateTimeOffset now, int maxCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Parks inside the model call - after the trusted context is assembled and before anything is
    /// dispatched - so a test can drive the exact window in which a conversation rotates while the
    /// Turn holds no proposal yet. The handoffs are completed sources rather than sleeps, so the race
    /// is deterministic; <see cref="ReachedAsync"/> bounds the wait only so a regression that never
    /// reaches the model call fails the test instead of hanging the run.
    /// </summary>
    private sealed class GatedModelBoundary(IModelBoundary inner) : IModelBoundary
    {
        private static readonly TimeSpan ReachTimeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the Turn is parked inside the model call.</summary>
        public Task ReachedAsync() => _reached.Task.WaitAsync(ReachTimeout);

        public void Release() => _released.TrySetResult();

        public async Task<ModelProposal> ProposeAsync(
            InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken)
        {
            _reached.TrySetResult();
            await _released.Task;

            return await inner.ProposeAsync(turn, context, cancellationToken);
        }
    }

    /// <summary>
    /// Everything one coordinator is wired over, so a test that is about what processing does to
    /// Inventory state can seed and read the very stores the dispatch runs against.
    /// </summary>
    private sealed record CoordinatorHarness(
        TurnProcessingCoordinator Coordinator,
        InMemoryInboxStore Inbox,
        InMemoryOutcomeStore Outcomes,
        InMemoryDeliveryStore Deliveries,
        InMemoryTurnResultStore ResultStore,
        InMemoryFoundryConversationBindingStore Bindings,
        InMemoryConfirmationProposalStore Proposals,
        InMemoryInventoryStore Inventories,
        InMemoryActiveInventorySelectionStore Selections,
        InMemoryStockStore Stock,
        InMemoryInventoryReferenceStore References);

    private static (TurnProcessingCoordinator Coordinator, InMemoryInboxStore Inbox, InMemoryOutcomeStore Outcomes, InMemoryDeliveryStore Deliveries, InMemoryTurnResultStore ResultStore, InMemoryFoundryConversationBindingStore Bindings)
        CreateCoordinator(
            TimeProvider timeProvider,
            IModelBoundary? modelBoundary = null,
            InMemoryTurnProgressEventStore? progressStore = null,
            ITurnProgressEventStore? progressEventStore = null)
    {
        var harness = CreateHarness(timeProvider, modelBoundary, progressStore, progressEventStore);

        return (harness.Coordinator, harness.Inbox, harness.Outcomes, harness.Deliveries, harness.ResultStore, harness.Bindings);
    }

    private static CoordinatorHarness CreateHarness(
        TimeProvider timeProvider,
        IModelBoundary? modelBoundary = null,
        InMemoryTurnProgressEventStore? progressStore = null,
        ITurnProgressEventStore? progressEventStore = null)
    {
        var inbox = new InMemoryInboxStore();
        var outcomes = new InMemoryOutcomeStore();
        var deliveries = new InMemoryDeliveryStore();
        var resultStore = new InMemoryTurnResultStore(inbox, outcomes, deliveries);
        progressStore ??= new InMemoryTurnProgressEventStore();
        var leases = new InMemoryLeaseCoordinator(timeProvider);

        // Real, minimally wired instances: tests whose content is "hello" or the scripted failure
        // marker never leave the Direct path, and tests that seed Inventory state drive the very same
        // dispatch production does, through exactly these stores.
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        var selectionStore = new InMemoryActiveInventorySelectionStore();
        var auditStore = new InMemoryInventoryAuthorizationAuditStore(selectionStore);
        var authorizationService = new InventoryAuthorizationService(inventoryStore, auditStore);
        var proposalStore = new InMemoryConfirmationProposalStore();
        var selectionService = new InventorySelectionService(authorizationService, selectionStore, proposalStore);
        var bindingStore = new InMemoryFoundryConversationBindingStore();
        var executionContextFactory = new TurnExecutionContextFactory(inbox, bindingStore, selectionService);
        var stockStore = new InMemoryStockStore();
        var referenceStore = new InMemoryInventoryReferenceStore();
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
            progressEventStore ?? progressStore,
            leases,
            new ProgressObservingModelBoundary(modelBoundary ?? new ScriptedModelBoundary(), progressStore),
            executionContextFactory,
            new ConfirmationProposalLifecycle(proposalStore, bindingStore),
            toolDispatcher,
            timeProvider,
            NullLogger<TurnProcessingCoordinator>.Instance);

        return new CoordinatorHarness(
            coordinator,
            inbox,
            outcomes,
            deliveries,
            resultStore,
            bindingStore,
            proposalStore,
            inventoryStore,
            selectionStore,
            stockStore,
            referenceStore);
    }

    /// <summary>
    /// Accepts a Turn the way production does: into the Foundry conversation its (Participant,
    /// ChannelConversation) pair currently holds, resolved from the very same binding store the
    /// coordinator's execution context reads back through.
    /// </summary>
    private static async Task AcceptAsync(
        InMemoryInboxStore inbox, InMemoryFoundryConversationBindingStore bindings, InboundTurn turn)
    {
        var binding = await bindings.GetOrCreateAsync(
            turn.ParticipantId, turn.ChannelConversationId, turn.ReceivedAt, CancellationToken.None);

        await inbox.AcceptAsync(turn, binding, CancellationToken.None);
    }

    [Fact]
    public async Task A_progress_append_failure_does_not_block_terminal_outcomes_or_later_turns_in_the_conversation()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var progressStore = new InMemoryTurnProgressEventStore { FailNextAppend = true };
        var (coordinator, inbox, outcomes, _, _, _) = CreateCoordinator(timeProvider, progressStore: progressStore);
        var firstTurn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        var laterTurn = TestTurns.Text(
            "native-2", SomeParticipant, "conversation-1", "hello again", null, Now.AddSeconds(1), null);
        await inbox.AcceptAsync(firstTurn, CancellationToken.None);
        await inbox.AcceptAsync(laterTurn, CancellationToken.None);

        var processedCount = await coordinator.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal(2, processedCount);
        Assert.Equal(OutcomeStatus.Completed, (await outcomes.FindAsync(firstTurn.TurnId, CancellationToken.None))!.Status);
        Assert.Equal(OutcomeStatus.Completed, (await outcomes.FindAsync(laterTurn.TurnId, CancellationToken.None))!.Status);
        Assert.Empty(await progressStore.ReadAsync(firstTurn.TurnId, CancellationToken.None));
        Assert.Single(await progressStore.ReadAsync(laterTurn.TurnId, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_from_progress_publication_propagates()
    {
        var timeProvider = new FakeTimeProvider(Now);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var (coordinator, inbox, _, _, _, _) = CreateCoordinator(
            timeProvider,
            progressEventStore: new CancelingProgressEventStore());
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await inbox.AcceptAsync(turn, CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => coordinator.ProcessPendingAsync(cancellationSource.Token));
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
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await coordinator.ProcessPendingAsync(CancellationToken.None));

        var progressEvent = Assert.Single(await progressStore.ReadAsync(turn.TurnId, CancellationToken.None));
        Assert.Equal(turn.TurnId, progressEvent.TurnId);
        Assert.Equal(TurnEventSequence.Processing, progressEvent.Sequence);
        Assert.Equal(Now, progressEvent.OccurredAt);
        Assert.Equal(Now + TurnProgressEvent.Retention, progressEvent.ExpiresAt);
        Assert.NotEqual(timeProvider.GetUtcNow(), progressEvent.OccurredAt);
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
    // directly, with no tool call. That conversation is now settled at acceptance, so what this
    // proves is that the direct-answer path still carries it all the way to the model boundary:
    // losing it there would deny the model the conversation it must continue, and would split a
    // conversation's history by what its Turns happened to ask for.
    [Fact]
    public async Task A_directly_answered_turn_still_establishes_its_foundry_conversation_binding()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var capturing = new CapturingModelBoundary(new ScriptedModelBoundary());
        var (coordinator, inbox, _, _, _, bindings) = CreateCoordinator(timeProvider, capturing);
        var turn = TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null);
        await AcceptAsync(inbox, bindings, turn);

        await coordinator.ProcessPendingAsync(CancellationToken.None);

        var binding = Assert.Single(bindings.Bindings);
        Assert.Equal(SomeParticipant, binding.ParticipantId);
        Assert.Equal(turn.ChannelConversationId, binding.ChannelConversationId);
        Assert.Equal(binding.FoundryConversationId, Assert.Single(capturing.Invocations).Foundry);
    }

    [Fact]
    public async Task Model_planning_is_given_the_trusted_foundry_conversation_stable_per_participant_and_conversation()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var capturing = new CapturingModelBoundary(new ScriptedModelBoundary());
        var (coordinator, inbox, _, _, _, bindings) = CreateCoordinator(timeProvider, capturing);
        await AcceptAsync(inbox, bindings, TestTurns.Text("native-1", SomeParticipant, "conversation-1", "hello", null, Now, null));
        await AcceptAsync(inbox, bindings, TestTurns.Text("native-2", SomeParticipant, "conversation-1", "hello again", null, Now, null));
        await AcceptAsync(inbox, bindings, TestTurns.Text("native-3", SomeParticipant, "conversation-2", "hello there", null, Now, null));

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

    private static readonly InventoryId SomeInventory = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly UnitId EachUnit = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private const string MutationConversation = "conversation-1";

    /// <summary>
    /// Seeds exactly what a real mutation needs to get as far as asking for confirmation: an
    /// Inventory the Participant may edit, that Inventory selected as the conversation's Active one,
    /// its reserved <c>each</c> Unit, and one empty Stock Entry a Forget can name. Without all four
    /// the dispatch would answer no_active_inventory, forbidden, not_found, or
    /// forget_requires_zero_quantity - and a test asserting "nothing confirmable is left behind"
    /// would pass without any confirmation ever having been possible.
    /// </summary>
    private static async Task SeedForgettableStockAsync(CoordinatorHarness harness)
    {
        harness.Inventories.GrantMembership(SomeInventory, SomeParticipant, MembershipRole.Editor, Now);
        harness.References.AddUnit(SomeInventory, EachUnit, "each");
        harness.Stock.CreateRow(SomeInventory, "Steel Bolts", EachUnit, "each", null, null, null, Quantity.Zero);
        await harness.Selections.UpsertAsync(
            new ActiveInventorySelection(SomeParticipant, MutationConversation, SomeInventory, Now),
            CancellationToken.None);
    }

    private static InboundTurn ForgetTurn(string nativeMessageId, DateTimeOffset receivedAt) =>
        TestTurns.Text(nativeMessageId, SomeParticipant, MutationConversation, "forget stock Steel Bolts", null, receivedAt, null);

    /// <summary>
    /// Starts a new conversation the way the durable rotation does it: the generation advances and
    /// whatever was pending in the conversation being left behind stops being confirmable, as one
    /// step. <see cref="IConversationRotationStore"/> owns that atomicity in production; this states
    /// the same two effects so a coordinator test can reset a conversation without a database.
    /// </summary>
    private static async Task ResetConversationAsync(CoordinatorHarness harness, DateTimeOffset now)
    {
        harness.Bindings.Rotate(SomeParticipant, new ChannelConversationId(MutationConversation), now);
        await harness.Proposals.InvalidatePendingAsync(
            SomeParticipant, MutationConversation, ProposalStatus.ConversationReset, now, CancellationToken.None);
    }

    private static async Task<ConfirmationProposal?> PendingProposalAsync(CoordinatorHarness harness) =>
        await harness.Proposals.FindPendingAsync(SomeParticipant, MutationConversation, CancellationToken.None);

    // The property the whole post-dispatch settlement exists for: a mutation-capable Turn accepted
    // under one Foundry conversation generation must never leave a confirmable proposal behind once
    // the Participant has started a new conversation - even though the Turn itself was queued, and
    // dispatched, entirely legitimately.
    [Fact]
    public async Task A_mutation_accepted_before_a_reset_leaves_nothing_confirmable_in_the_new_conversation()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var harness = CreateHarness(timeProvider);
        await SeedForgettableStockAsync(harness);

        // First, prove the path is genuinely reachable: an identical Turn with no reset anywhere near
        // it really does reach confirmation and really does leave something confirmable behind.
        var beforeAnyReset = ForgetTurn("native-1", Now);
        await AcceptAsync(harness.Inbox, harness.Bindings, beforeAnyReset);
        await harness.Coordinator.ProcessPendingAsync(CancellationToken.None);

        var control = await harness.Outcomes.FindAsync(beforeAnyReset.TurnId, CancellationToken.None);
        Assert.Equal(OutcomeCategory.ConfirmationRequired, control!.Category);
        Assert.NotNull(await PendingProposalAsync(harness));

        // A second Turn is accepted into the same conversation - still the generation it was queued
        // under - and only then does the Participant start a new conversation.
        var acceptedBeforeTheReset = ForgetTurn("native-2", Now.AddSeconds(1));
        await AcceptAsync(harness.Inbox, harness.Bindings, acceptedBeforeTheReset);
        await ResetConversationAsync(harness, Now.AddSeconds(2));
        Assert.Null(await PendingProposalAsync(harness));

        await harness.Coordinator.ProcessPendingAsync(CancellationToken.None);

        // It dispatched against the history it was accepted under, as it must - but what it proposed
        // belongs to a conversation the Participant has walked away from, so nothing confirmable is
        // left and the answer says so rather than offering a token that would never work.
        Assert.Null(await PendingProposalAsync(harness));
        var outcome = await harness.Outcomes.FindAsync(acceptedBeforeTheReset.TurnId, CancellationToken.None);
        Assert.NotNull(outcome);
        Assert.NotEqual(OutcomeCategory.ConfirmationRequired, outcome!.Category);
        Assert.Equal(ConfirmationProposalLifecycle.ConversationResetCode, outcome.Code);
    }

    // The window the captured flag alone cannot close: at context assembly the conversation really
    // was current, and the reset lands while the Turn is inside the model call - before it has any
    // proposal for the rotation to settle. Only re-reading the binding after dispatch catches it.
    [Fact]
    public async Task A_reset_that_lands_while_a_turn_is_being_processed_still_settles_what_it_goes_on_to_propose()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var gate = new GatedModelBoundary(new ScriptedModelBoundary());
        var harness = CreateHarness(timeProvider, gate);
        await SeedForgettableStockAsync(harness);
        var turn = ForgetTurn("native-1", Now);
        await AcceptAsync(harness.Inbox, harness.Bindings, turn);

        var processing = harness.Coordinator.ProcessPendingAsync(CancellationToken.None);
        try
        {
            await gate.ReachedAsync();

            // The hole itself: the trusted context has already been assembled and found the
            // conversation current, and there is nothing for a reset to settle yet.
            Assert.Null(await PendingProposalAsync(harness));
            await ResetConversationAsync(harness, Now.AddSeconds(1));
        }
        finally
        {
            // Released even if an assertion above fails, so a failing test reports its assertion
            // rather than leaving the parked coordinator holding its lease forever.
            gate.Release();
        }

        Assert.Equal(1, await processing);

        // The proposal really was created - the dispatch ran in full - and then settled by the
        // post-dispatch re-read rather than left waiting for a "confirm" in a conversation that no
        // longer exists.
        var created = Assert.Single(harness.Proposals.Proposals);
        Assert.Equal(turn.TurnId, created.ProposedInTurnId);
        Assert.Equal(
            ProposalStatus.ConversationReset,
            await harness.Proposals.FindStatusAsync(created.Id, CancellationToken.None));
        Assert.Null(await PendingProposalAsync(harness));
        var outcome = await harness.Outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.NotEqual(OutcomeCategory.ConfirmationRequired, outcome!.Category);
    }

    // A reset accepted before this Turn had proposed anything is only one of the two orders. In the
    // other, this Turn's proposal is already durable when the reset runs, so the reset settles it -
    // and there is nothing left for the post-dispatch pass to settle. The answer must still not offer
    // a confirmation: a token that has already stopped working is worse than no token at all.
    [Fact]
    public async Task A_reset_that_settles_the_proposal_itself_still_never_answers_with_a_confirmation()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var harness = CreateHarness(timeProvider);
        await SeedForgettableStockAsync(harness);
        var turn = ForgetTurn("native-1", Now);
        await AcceptAsync(harness.Inbox, harness.Bindings, turn);

        // The reset lands in the instant right after this Turn's proposal became confirmable, exactly
        // as the durable rotation would: it finds the proposal and settles it in its own transaction.
        harness.Proposals.AfterStore = () => ResetConversationAsync(harness, Now.AddSeconds(1));

        Assert.Equal(1, await harness.Coordinator.ProcessPendingAsync(CancellationToken.None));

        var created = Assert.Single(harness.Proposals.Proposals);
        Assert.Equal(
            ProposalStatus.ConversationReset,
            await harness.Proposals.FindStatusAsync(created.Id, CancellationToken.None));
        Assert.Null(await PendingProposalAsync(harness));

        var outcome = await harness.Outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.NotEqual(OutcomeCategory.ConfirmationRequired, outcome!.Category);
        Assert.Equal(ConfirmationProposalLifecycle.ConversationResetCode, outcome.Code);

        // No payload and no delivery may still carry the single-use token the proposal was offered
        // with, in either the answer that is recorded or the one that is sent.
        Assert.Null(outcome.Payload);
        Assert.All(
            harness.Deliveries.Deliveries,
            delivery => Assert.DoesNotContain("token", delivery.Payload, StringComparison.OrdinalIgnoreCase));
    }

    // A reset ends what can still be confirmed, not what has already been asked. A read accepted
    // before it proposes nothing, so it simply answers - from the conversation it was accepted under.
    [Fact]
    public async Task A_read_accepted_before_a_reset_still_answers_from_the_conversation_it_was_accepted_under()
    {
        var timeProvider = new FakeTimeProvider(Now);
        var capturing = new CapturingModelBoundary(new ScriptedModelBoundary());
        var harness = CreateHarness(timeProvider, capturing);
        await SeedForgettableStockAsync(harness);
        var turn = TestTurns.Text("native-1", SomeParticipant, MutationConversation, "list stock including zero", null, Now, null);
        var acceptedUnder = await harness.Bindings.GetOrCreateAsync(
            SomeParticipant, turn.ChannelConversationId, Now, CancellationToken.None);
        await harness.Inbox.AcceptAsync(turn, acceptedUnder, CancellationToken.None);

        await ResetConversationAsync(harness, Now.AddSeconds(1));
        Assert.Equal(1, await harness.Coordinator.ProcessPendingAsync(CancellationToken.None));

        var outcome = await harness.Outcomes.FindAsync(turn.TurnId, CancellationToken.None);
        Assert.Equal(OutcomeStatus.Completed, outcome!.Status);
        Assert.Equal(OutcomeCategory.Completed, outcome.Category);
        Assert.Contains("Steel Bolts", outcome.Payload);
        Assert.Equal(acceptedUnder.FoundryConversationId, Assert.Single(capturing.Invocations).Foundry);
        Assert.Empty(harness.Proposals.Proposals);
    }
}
