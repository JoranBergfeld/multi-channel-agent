using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class InventoryGovernancePolicyTests
{
    private static readonly ParticipantId Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Other = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Theory]
    [InlineData(MembershipRole.Viewer, true)]
    [InlineData(MembershipRole.Editor, true)]
    [InlineData(MembershipRole.Owner, false)]
    public void IsGrantableRole_only_allows_viewer_or_editor(MembershipRole role, bool expected)
    {
        Assert.Equal(expected, InventoryGovernancePolicy.IsGrantableRole(role));
    }

    [Fact]
    public void IsSelfTransfer_is_true_when_target_equals_current_owner()
    {
        Assert.True(InventoryGovernancePolicy.IsSelfTransfer(Owner, Owner));
    }

    [Fact]
    public void IsSelfTransfer_is_false_when_target_differs_from_current_owner()
    {
        Assert.False(InventoryGovernancePolicy.IsSelfTransfer(Owner, Other));
    }

    [Fact]
    public void IsOrphaned_is_true_only_when_the_owner_participant_is_not_active()
    {
        Assert.True(InventoryGovernancePolicy.IsOrphaned(ownerIsActive: false));
        Assert.False(InventoryGovernancePolicy.IsOrphaned(ownerIsActive: true));
    }

    [Theory]
    [InlineData(MembershipRole.Owner, MembershipRole.Owner, true)]
    [InlineData(MembershipRole.Owner, MembershipRole.Editor, true)]
    [InlineData(MembershipRole.Owner, MembershipRole.Viewer, true)]
    [InlineData(MembershipRole.Editor, MembershipRole.Owner, false)]
    [InlineData(MembershipRole.Editor, MembershipRole.Editor, true)]
    [InlineData(MembershipRole.Editor, MembershipRole.Viewer, true)]
    [InlineData(MembershipRole.Viewer, MembershipRole.Owner, false)]
    [InlineData(MembershipRole.Viewer, MembershipRole.Editor, false)]
    [InlineData(MembershipRole.Viewer, MembershipRole.Viewer, true)]
    public void Satisfies_reflects_the_owner_greater_than_editor_greater_than_viewer_hierarchy(
        MembershipRole actualRole, MembershipRole requiredRole, bool expected)
    {
        Assert.Equal(expected, InventoryGovernancePolicy.Satisfies(actualRole, requiredRole));
    }

    [Fact]
    public void CanRemoveMember_rejects_removing_the_current_owner()
    {
        Assert.False(InventoryGovernancePolicy.CanRemoveMember(MembershipRole.Owner));
    }

    [Theory]
    [InlineData(MembershipRole.Viewer)]
    [InlineData(MembershipRole.Editor)]
    public void CanRemoveMember_allows_removing_non_owner_members(MembershipRole role)
    {
        Assert.True(InventoryGovernancePolicy.CanRemoveMember(role));
    }
}
