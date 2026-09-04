using MultiChannelAgent.Domain.Inventories;
using MultiChannelAgent.Domain.Turns;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class StockOperationIdTests
{
    private static readonly TurnId SomeTurn = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly TurnId AnotherTurn = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void The_same_turn_and_tool_always_derive_the_same_operation_identity()
    {
        var first = StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0);
        var second = StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0);

        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first.Value);
    }

    [Fact]
    public void A_different_turn_derives_a_different_operation_identity()
    {
        Assert.NotEqual(
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0),
            StockOperationId.Derive(AnotherTurn, "add_stock", sequence: 0));
    }

    [Fact]
    public void A_different_tool_in_the_same_turn_derives_a_different_operation_identity()
    {
        Assert.NotEqual(
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0),
            StockOperationId.Derive(SomeTurn, "remove_stock", sequence: 0));
    }

    [Fact]
    public void A_later_call_of_the_same_tool_in_the_same_turn_derives_a_different_operation_identity()
    {
        Assert.NotEqual(
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0),
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 1));
    }

    [Fact]
    public void The_derivation_is_stable_across_processes_not_merely_within_one()
    {
        // A hard-coded expectation: if the derivation ever changes, a Turn retried by a NEWER build
        // would derive a different identity and could apply its effect a second time. That is exactly
        // the failure this identity exists to prevent, so it is pinned here deliberately.
        Assert.Equal(
            "8a9ee7f1-c583-ca1b-37fa-9d90d543eb10",
            StockOperationId.Derive(SomeTurn, "add_stock", sequence: 0).Value.ToString());
    }
    [Fact]
    public void A_proposals_execution_identity_is_derived_and_therefore_survives_a_restart()
    {
        var proposalId = new ProposalId(Guid.NewGuid());

        Assert.Equal(StockOperationId.DeriveForProposal(proposalId), StockOperationId.DeriveForProposal(proposalId));
    }

    [Fact]
    public void Two_proposals_never_share_an_execution_identity()
    {
        Assert.NotEqual(
            StockOperationId.DeriveForProposal(new ProposalId(Guid.NewGuid())),
            StockOperationId.DeriveForProposal(new ProposalId(Guid.NewGuid())));
    }

    [Fact]
    public void A_proposals_execution_identity_can_never_collide_with_a_Turns_tool_identity()
    {
        // The two derivations hash differently shaped material, so no Turn/tool/sequence triple can
        // ever produce a proposal's identity - the two ledgers stay disjoint by construction.
        var shared = Guid.NewGuid();

        Assert.NotEqual(
            StockOperationId.DeriveForProposal(new ProposalId(shared)),
            StockOperationId.Derive(new TurnId(shared), "confirm_inventory_operation", sequence: 0));
    }
}
