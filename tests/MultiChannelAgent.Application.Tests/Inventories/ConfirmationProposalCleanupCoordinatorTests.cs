using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Application.Tests.Inventories;

public sealed class ConfirmationProposalCleanupCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly ParticipantId Participant = new(Guid.NewGuid());
    private static readonly InventoryId SomeInventory = new(Guid.NewGuid());
    private const string Conversation = "conversation-1";

    private sealed record Harness(
        ConfirmationProposalCleanupCoordinator Coordinator,
        InMemoryConfirmationProposalStore ProposalStore,
        InMemoryLeaseCoordinator Leases,
        FakeTimeProvider Time);

    private static Harness CreateHarness()
    {
        var proposalStore = new InMemoryConfirmationProposalStore();
        var time = new FakeTimeProvider(Now);
        var leases = new InMemoryLeaseCoordinator(time);

        return new Harness(
            new ConfirmationProposalCleanupCoordinator(
                proposalStore, leases, time, NullLogger<ConfirmationProposalCleanupCoordinator>.Instance),
            proposalStore,
            leases,
            time);
    }

    private static ConfirmationProposal Proposal(string? conversation = null)
    {
        var stockEntryId = new StockEntryId(Guid.NewGuid());

        return ConfirmationProposal.Create(
            ConfirmationToken.HashOf(ConfirmationToken.Issue()),
            Participant,
            conversation ?? Conversation,
            SomeInventory,
            TurnId.NewId(),
            [
                new ProposedChange
                {
                    Order = 1,
                    Kind = StockMutationKind.Forget,
                    Effect = StockChangeEffectKind.Forgotten,
                    Source = new ProposedEntryState(
                        stockEntryId, "Steel Bolts", "steel bolts", new UnitId(Guid.NewGuid()), "each",
                        null, null, null, Quantity.Zero, Quantity.Zero, Retired: true),
                },
            ],
            [new ExpectedEntryVersion(stockEntryId, Guid.NewGuid())],
            [],
            Now);
    }

    [Fact]
    public async Task A_pass_expires_pending_proposals_whose_ten_minutes_have_run_out()
    {
        var harness = CreateHarness();
        var proposal = Proposal();
        await harness.ProposalStore.StoreAsync(proposal, Now, CancellationToken.None);

        Assert.Equal(0, await harness.Coordinator.SweepAsync(CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));

        harness.Time.SetUtcNow(Now.AddMinutes(ConfirmationProposal.LifetimeMinutes));

        Assert.Equal(1, await harness.Coordinator.SweepAsync(CancellationToken.None));
        Assert.Equal(ProposalStatus.Expired, await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_pass_deletes_settled_proposals_past_retention_and_keeps_newer_ones()
    {
        var harness = CreateHarness();
        var old = Proposal();
        var recent = Proposal("conversation-2");
        await harness.ProposalStore.StoreAsync(old, Now, CancellationToken.None);
        await harness.ProposalStore.SettleAsync(old.Id, ProposalStatus.Rejected, Now, CancellationToken.None);
        await harness.ProposalStore.StoreAsync(recent, Now, CancellationToken.None);
        await harness.ProposalStore.SettleAsync(
            recent.Id, ProposalStatus.Rejected, Now.Add(ConfirmationProposalCleanupCoordinator.SettledRetention), CancellationToken.None);

        harness.Time.SetUtcNow(Now.Add(ConfirmationProposalCleanupCoordinator.SettledRetention).AddMinutes(1));

        Assert.Equal(1, await harness.Coordinator.SweepAsync(CancellationToken.None));
        Assert.Null(await harness.ProposalStore.FindStatusAsync(old.Id, CancellationToken.None));
        Assert.Equal(ProposalStatus.Rejected, await harness.ProposalStore.FindStatusAsync(recent.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_pass_that_cannot_take_the_lease_does_nothing()
    {
        var harness = CreateHarness();
        var proposal = Proposal();
        await harness.ProposalStore.StoreAsync(proposal, Now, CancellationToken.None);
        harness.Time.SetUtcNow(Now.AddHours(48));

        var held = await harness.Leases.TryAcquireAsync(
            "confirmation-proposal-cleanup", "another-replica", TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(held);

        Assert.Equal(0, await harness.Coordinator.SweepAsync(CancellationToken.None));
        Assert.Equal(ProposalStatus.Pending, await harness.ProposalStore.FindStatusAsync(proposal.Id, CancellationToken.None));
    }
}
