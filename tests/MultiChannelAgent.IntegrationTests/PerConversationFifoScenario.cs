using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Turns;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.IntegrationTests;

/// <summary>
/// The per-ChannelConversation FIFO guarantee, exercised end to end through the real HTTP boundary
/// and a real relational engine: a Turn that cannot reach a terminal Outcome keeps its own
/// conversation's later Turns completely untouched - not merely unfinished, but never even proposed
/// to the model - across repeated passes and lease acquisitions, while an unrelated Participant's
/// conversation keeps completing normally. Every assertion is on Turn identities and the order they
/// were actually processed in, never on recorded timestamps (which can collide and so could pass
/// even for an out-of-order run).
/// </summary>
internal static class PerConversationFifoScenario
{
    /// <summary>Content only this scenario submits; the wrapper below fails exactly these Turns while a fault is injected.</summary>
    private const string StuckContent = "make this turn get stuck";

    /// <summary>Fails the injected Turn and records the identity of every Turn the model is asked to plan, in order.</summary>
    private sealed class RecordingModelBoundary(IModelBoundary inner, List<Guid> planned, Func<bool> faultInjected) : IModelBoundary
    {
        public Task<ModelProposal> ProposeAsync(InboundTurn turn, ModelInvocationContext context, CancellationToken cancellationToken)
        {
            planned.Add(turn.TurnId.Value);

            if (faultInjected() && turn.ContentText == StuckContent)
            {
                throw new InvalidOperationException("Injected fault: this Turn cannot reach a terminal Outcome yet.");
            }

            return inner.ProposeAsync(turn, context, cancellationToken);
        }
    }

    public static async Task RunAsync(WebApplicationFactory<Program> factory)
    {
        var httpClient = ConversationTestClient.CreateHttpsClient(factory);
        var stuck = await ConversationTestClient.SignInAsync(httpClient, "Stuck Conversation");
        var healthy = await ConversationTestClient.SignInAsync(httpClient, "Healthy Conversation");

        await stuck.CreateAndSelectInventoryAsync("Stuck Warehouse");
        await healthy.CreateAndSelectInventoryAsync("Healthy Warehouse");

        var blockedHead = await stuck.SubmitAcceptedTurnAsync("native-fifo-stuck-1", StuckContent);
        var blockedSuccessor = await stuck.SubmitAcceptedTurnAsync("native-fifo-stuck-2", "list stock");
        var unrelated = await healthy.SubmitAcceptedTurnAsync("native-fifo-healthy-1", "list stock");

        var planned = new List<Guid>();
        var faultInjected = true;

        async Task<int> RunPassAsync()
        {
            // A fresh scope per pass, exactly as the hosted worker gets: this is also what makes each
            // pass acquire the processing lease anew, so the invariant is proven across lease
            // boundaries rather than only within one long-held lease.
            using var scope = factory.Services.CreateScope();
            var services = scope.ServiceProvider;

            var coordinator = new TurnProcessingCoordinator(
                services.GetRequiredService<IInboxStore>(),
                services.GetRequiredService<ITurnResultStore>(),
                services.GetRequiredService<ITurnProgressEventStore>(),
                services.GetRequiredService<ILeaseCoordinator>(),
                new RecordingModelBoundary(services.GetRequiredService<IModelBoundary>(), planned, () => faultInjected),
                services.GetRequiredService<TurnExecutionContextFactory>(),
                services.GetRequiredService<ConfirmationProposalLifecycle>(),
                services.GetRequiredService<IToolDispatcher>(),
                services.GetRequiredService<TimeProvider>(),
                NullLogger<TurnProcessingCoordinator>.Instance);

            return await coordinator.ProcessPendingAsync(CancellationToken.None);
        }

        // Pass one: the stuck conversation's head fails, so only the unrelated conversation completes.
        Assert.Equal(1, await RunPassAsync());
        Assert.Null(await stuck.GetOutcomeAsync(blockedHead));
        Assert.Null(await stuck.GetOutcomeAsync(blockedSuccessor));
        Assert.Equal("completed", (await healthy.GetOutcomeAsync(unrelated))!.Value.GetProperty("status").GetString());

        // Pass two, a separate lease acquisition: the successor is still never even planned - it is
        // not merely left unfinished, it is never claimed while its predecessor is outstanding.
        Assert.Equal(0, await RunPassAsync());
        Assert.DoesNotContain(blockedSuccessor, planned);
        Assert.Equal(2, planned.Count(id => id == blockedHead));
        Assert.Null(await stuck.GetOutcomeAsync(blockedSuccessor));

        // And that is a property of the durable claim itself, not of any worker's bookkeeping: asked
        // directly, with a batch limit far larger than the backlog and no worker state at all, the
        // inbox still refuses to offer the successor while its predecessor is outstanding.
        using (var claimScope = factory.Services.CreateScope())
        {
            var claimed = await claimScope.ServiceProvider
                .GetRequiredService<IInboxStore>()
                .ClaimPendingAsync(50, CancellationToken.None);
            var claimedIds = claimed.Select(t => t.TurnId.Value).ToList();

            Assert.Contains(blockedHead, claimedIds);
            Assert.DoesNotContain(blockedSuccessor, claimedIds);
        }

        // Once the fault clears, the conversation resumes exactly where it left off: its head first,
        // then - and only then - its successor.
        faultInjected = false;
        Assert.Equal(2, await RunPassAsync());

        Assert.Equal([blockedHead, blockedSuccessor], planned.Skip(planned.Count - 2).ToList());
        Assert.True(planned.LastIndexOf(blockedHead) < planned.IndexOf(blockedSuccessor));
        Assert.Equal("completed", (await stuck.GetOutcomeAsync(blockedHead))!.Value.GetProperty("status").GetString());

        var successorOutcome = await stuck.GetOutcomeAsync(blockedSuccessor);
        Assert.Equal("completed", successorOutcome!.Value.GetProperty("status").GetString());
        Assert.Equal("stock_list", successorOutcome.Value.GetProperty("payload").GetProperty("kind").GetString());
    }
}
