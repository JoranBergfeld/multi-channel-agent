using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryMembershipServiceTests
{
    private static readonly ParticipantId Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Editor = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Recipient = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly ParticipantId NonMember = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InventoryMembershipService Service,
        InMemoryInventoryStore InventoryStore,
        InMemoryTenantMemberDirectory Directory,
        InMemoryInventoryMembershipStore MembershipStore,
        InventoryId InventoryId);

    private static Fixture CreateFixture()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        var inventoryId = new InventoryId(Guid.NewGuid());
        inventoryStore.GrantMembership(inventoryId, Owner, MembershipRole.Owner, Now);
        inventoryStore.GrantMembership(inventoryId, Editor, MembershipRole.Editor, Now);

        var selectionStore = new InMemoryActiveInventorySelectionStore();
        var authorizationService = new InventoryAuthorizationService(
            inventoryStore, new InMemoryInventoryAuthorizationAuditStore(selectionStore));

        var directory = new InMemoryTenantMemberDirectory();
        directory.Register(new ResolvedTenantMember(Recipient, "Recipient Person"));
        directory.Register(new ResolvedTenantMember(Owner, "Owner Name"));

        var participantStore = new InMemoryParticipantStore();
        var membershipStore = new InMemoryInventoryMembershipStore(inventoryStore);
        var service = new InventoryMembershipService(authorizationService, directory, participantStore, membershipStore);

        return new Fixture(service, inventoryStore, directory, membershipStore, inventoryId);
    }

    [Fact]
    public async Task Owner_can_grant_viewer_to_a_resolvable_active_tenant_member_without_their_acceptance()
    {
        var f = CreateFixture();

        var result = await f.Service.GrantOrChangeAsync(Owner, f.InventoryId, Recipient.ToString(), MembershipRole.Viewer, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.Granted, result.Outcome);
        Assert.Equal(MembershipRole.Viewer, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Recipient).Role);
    }

    [Fact]
    public async Task Owner_can_change_an_existing_members_role()
    {
        var f = CreateFixture();
        await f.Service.GrantOrChangeAsync(Owner, f.InventoryId, Recipient.ToString(), MembershipRole.Viewer, Now, CancellationToken.None);

        var result = await f.Service.GrantOrChangeAsync(Owner, f.InventoryId, Recipient.ToString(), MembershipRole.Editor, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.RoleChanged, result.Outcome);
        Assert.Equal(MembershipRole.Editor, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Recipient).Role);
    }

    [Fact]
    public async Task Granting_owner_role_is_rejected_as_an_invalid_role()
    {
        var f = CreateFixture();

        var result = await f.Service.GrantOrChangeAsync(Owner, f.InventoryId, Recipient.ToString(), MembershipRole.Owner, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.InvalidRole, result.Outcome);
    }

    [Fact]
    public async Task Granting_a_role_to_the_current_owner_is_rejected_use_transfer_instead()
    {
        var f = CreateFixture();

        var result = await f.Service.GrantOrChangeAsync(Owner, f.InventoryId, Owner.ToString(), MembershipRole.Editor, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.TargetIsOwner, result.Outcome);
    }

    [Fact]
    public async Task Granting_to_an_identifier_that_does_not_resolve_is_rejected()
    {
        var f = CreateFixture();

        var result = await f.Service.GrantOrChangeAsync(Owner, f.InventoryId, "unknown@example.com", MembershipRole.Viewer, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.TargetNotResolved, result.Outcome);
    }

    [Fact]
    public async Task A_non_member_requester_is_refused_non_disclosing()
    {
        var f = CreateFixture();

        var result = await f.Service.GrantOrChangeAsync(NonMember, f.InventoryId, Recipient.ToString(), MembershipRole.Viewer, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.RequesterNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task A_non_owner_member_requester_is_forbidden()
    {
        var f = CreateFixture();

        var result = await f.Service.GrantOrChangeAsync(Editor, f.InventoryId, Recipient.ToString(), MembershipRole.Viewer, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.RequesterNotOwner, result.Outcome);
    }

    [Fact]
    public async Task Owner_can_remove_a_non_owner_member()
    {
        var f = CreateFixture();

        var result = await f.Service.RemoveAsync(Owner, f.InventoryId, Editor, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.Removed, result.Outcome);
        Assert.DoesNotContain(f.InventoryStore.Memberships, m => m.ParticipantId == Editor);
    }

    // The current Owner can never be removed/demoted through the ordinary membership-removal
    // endpoint - ownership transfer is the sole path.
    [Fact]
    public async Task Owner_cannot_remove_themselves_through_the_ordinary_removal_path()
    {
        var f = CreateFixture();

        var result = await f.Service.RemoveAsync(Owner, f.InventoryId, Owner, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.TargetIsOwner, result.Outcome);
        Assert.Equal(MembershipRole.Owner, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Owner).Role);
    }

    [Fact]
    public async Task Removing_a_participant_who_is_not_a_member_reports_not_a_member()
    {
        var f = CreateFixture();

        var result = await f.Service.RemoveAsync(Owner, f.InventoryId, NonMember, Now, CancellationToken.None);

        Assert.Equal(MembershipRequestOutcome.TargetNotAMember, result.Outcome);
    }

    [Fact]
    public async Task ListMembers_is_owner_only_and_a_non_owner_is_forbidden()
    {
        var f = CreateFixture();

        var result = await f.Service.ListMembersAsync(Editor, f.InventoryId, Now, CancellationToken.None);

        Assert.Equal(MembershipListOutcome.RequesterNotOwner, result.Outcome);
        Assert.Null(result.Members);
    }

    [Fact]
    public async Task ListMembers_returns_the_roster_for_the_owner()
    {
        var f = CreateFixture();

        var result = await f.Service.ListMembersAsync(Owner, f.InventoryId, Now, CancellationToken.None);

        Assert.Equal(MembershipListOutcome.Listed, result.Outcome);
        Assert.Equal(2, result.Members!.Count);
    }
}
