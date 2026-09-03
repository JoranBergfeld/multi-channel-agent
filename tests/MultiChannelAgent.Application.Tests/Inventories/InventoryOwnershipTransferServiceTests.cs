using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryOwnershipTransferServiceTests
{
    private static readonly ParticipantId Owner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Editor = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Target = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly ParticipantId NonMember = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InventoryOwnershipTransferService Service, InMemoryInventoryStore InventoryStore, InventoryId InventoryId);

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
        directory.Register(new ResolvedTenantMember(Target, "Target Person"));
        directory.Register(new ResolvedTenantMember(Owner, "Owner Name"));
        directory.Register(new ResolvedTenantMember(Editor, "Editor Person"));

        var participantStore = new InMemoryParticipantStore();
        var ownershipStore = new InMemoryInventoryOwnershipStore(inventoryStore);
        var service = new InventoryOwnershipTransferService(authorizationService, directory, participantStore, ownershipStore);

        return new Fixture(service, inventoryStore, inventoryId);
    }

    [Fact]
    public async Task Owner_can_transfer_ownership_to_an_existing_participant_atomically()
    {
        var f = CreateFixture();

        var result = await f.Service.TransferAsync(Owner, f.InventoryId, Target.ToString(), Now, CancellationToken.None);

        Assert.Equal(TransferRequestOutcome.Transferred, result.Outcome);
        Assert.Equal(MembershipRole.Owner, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Target).Role);
        // The previous Owner is demoted to Editor, preserving their access, rather than removed.
        Assert.Equal(MembershipRole.Editor, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Owner).Role);
        // Exactly one Owner remains at all times.
        Assert.Single(f.InventoryStore.Memberships, m => m.InventoryId == f.InventoryId && m.Role == MembershipRole.Owner);
    }

    [Fact]
    public async Task Transferring_to_an_existing_editor_updates_their_membership_in_place_rather_than_duplicating_it()
    {
        var f = CreateFixture();

        var result = await f.Service.TransferAsync(Owner, f.InventoryId, Editor.ToString(), Now, CancellationToken.None);

        Assert.Equal(TransferRequestOutcome.Transferred, result.Outcome);
        Assert.Equal(MembershipRole.Owner, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Editor).Role);
        Assert.Equal(MembershipRole.Editor, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Owner).Role);
        Assert.Equal(2, f.InventoryStore.Memberships.Count(m => m.InventoryId == f.InventoryId));
    }

    [Fact]
    public async Task Transferring_to_oneself_is_rejected_as_a_conflict_not_a_silent_no_op()
    {
        var f = CreateFixture();

        var result = await f.Service.TransferAsync(Owner, f.InventoryId, Owner.ToString(), Now, CancellationToken.None);

        Assert.Equal(TransferRequestOutcome.SelfTransferRejected, result.Outcome);
        Assert.Equal(MembershipRole.Owner, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Owner).Role);
    }

    [Fact]
    public async Task Transferring_to_an_unresolvable_identifier_is_rejected()
    {
        var f = CreateFixture();

        var result = await f.Service.TransferAsync(Owner, f.InventoryId, "unknown@example.com", Now, CancellationToken.None);

        Assert.Equal(TransferRequestOutcome.TargetNotResolved, result.Outcome);
    }

    [Fact]
    public async Task A_non_member_requester_is_refused_non_disclosing()
    {
        var f = CreateFixture();

        var result = await f.Service.TransferAsync(NonMember, f.InventoryId, Target.ToString(), Now, CancellationToken.None);

        Assert.Equal(TransferRequestOutcome.RequesterNotAuthorized, result.Outcome);
    }

    [Fact]
    public async Task A_non_owner_member_requester_is_forbidden()
    {
        var f = CreateFixture();

        var result = await f.Service.TransferAsync(Editor, f.InventoryId, Target.ToString(), Now, CancellationToken.None);

        Assert.Equal(TransferRequestOutcome.RequesterNotOwner, result.Outcome);
    }
}
