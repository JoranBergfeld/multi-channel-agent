using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryRecoveryServiceTests
{
    private static readonly ParticipantId HealthyOwner = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId OrphanedOwner = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Recovered = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private const string ActorId = "recovery-admin-claim-value";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InventoryRecoveryService Service,
        InMemoryInventoryStore InventoryStore,
        InMemoryInventoryRecoveryStore RecoveryStore,
        InventoryId HealthyInventoryId,
        InventoryId OrphanedInventoryId);

    private static Fixture CreateFixture()
    {
        var inventoryStore = new InMemoryInventoryStore(_ => "Owner Name");
        var healthyInventoryId = new InventoryId(Guid.NewGuid());
        var orphanedInventoryId = new InventoryId(Guid.NewGuid());
        inventoryStore.GrantMembership(healthyInventoryId, HealthyOwner, MembershipRole.Owner, Now);
        inventoryStore.GrantMembership(orphanedInventoryId, OrphanedOwner, MembershipRole.Owner, Now);

        var participantStore = new InMemoryParticipantStore();
        participantStore.UpsertAsync(Participant.Create(HealthyOwner, "Healthy Owner"), CancellationToken.None);
        participantStore.UpsertAsync(Participant.Create(OrphanedOwner, "Orphaned Owner"), CancellationToken.None);

        var directory = new InMemoryTenantMemberDirectory();
        // HealthyOwner resolves (still active); OrphanedOwner is deliberately never registered, so
        // the directory reports them not found - the deterministic trigger for orphan status.
        directory.Register(new ResolvedTenantMember(HealthyOwner, "Healthy Owner"));
        directory.Register(new ResolvedTenantMember(Recovered, "Recovered Person"));

        var recoveryStore = new InMemoryInventoryRecoveryStore(inventoryStore, participantStore, directory);
        var service = new InventoryRecoveryService(recoveryStore);

        // Fold in each Inventory's own record (name/short id) so ListOrphanedAsync's summary is
        // meaningful, without the Owner-Membership side effect a real CreateAsync call would add on
        // top of the Memberships already granted directly above.
        inventoryStore.AddInventoryRecord(Inventory.Create("Healthy Warehouse", HealthyOwner, "seed-healthy", Now) with { Id = healthyInventoryId });
        inventoryStore.AddInventoryRecord(Inventory.Create("Orphaned Warehouse", OrphanedOwner, "seed-orphaned", Now) with { Id = orphanedInventoryId });

        return new Fixture(service, inventoryStore, recoveryStore, healthyInventoryId, orphanedInventoryId);
    }

    [Fact]
    public async Task ListOrphaned_excludes_healthy_inventories_whose_owner_is_still_active()
    {
        var f = CreateFixture();

        var page = await f.Service.ListOrphanedAsync(Now, CancellationToken.None);

        Assert.DoesNotContain(page.Items, i => i.InventoryId == f.HealthyInventoryId.ToString());
    }

    [Fact]
    public async Task ListOrphaned_includes_an_inventory_whose_owner_no_longer_resolves_as_active()
    {
        var f = CreateFixture();

        var page = await f.Service.ListOrphanedAsync(Now, CancellationToken.None);

        Assert.Contains(page.Items, i => i.InventoryId == f.OrphanedInventoryId.ToString());
    }

    [Fact]
    public async Task Recovering_a_healthy_inventory_is_not_eligible_non_disclosing()
    {
        var f = CreateFixture();

        var result = await f.Service.RecoverAsync(ActorId, f.HealthyInventoryId, Recovered.ToString(), Now, CancellationToken.None);

        Assert.Equal(RecoveryRequestOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task Recovering_a_nonexistent_inventory_is_the_same_not_eligible_outcome_as_healthy()
    {
        var f = CreateFixture();

        var result = await f.Service.RecoverAsync(ActorId, new InventoryId(Guid.NewGuid()), Recovered.ToString(), Now, CancellationToken.None);

        Assert.Equal(RecoveryRequestOutcome.NotEligible, result.Outcome);
    }

    [Fact]
    public async Task Recovering_an_orphaned_inventory_transfers_ownership_and_never_adds_the_admin_as_a_member()
    {
        var f = CreateFixture();

        var result = await f.Service.RecoverAsync(ActorId, f.OrphanedInventoryId, Recovered.ToString(), Now, CancellationToken.None);

        Assert.Equal(RecoveryRequestOutcome.Recovered, result.Outcome);
        Assert.Equal("Recovered Person", result.NewOwnerDisplayName);
        Assert.Equal(MembershipRole.Owner, f.InventoryStore.Memberships.Single(m => m.ParticipantId == Recovered).Role);
        Assert.Equal(MembershipRole.Editor, f.InventoryStore.Memberships.Single(m => m.ParticipantId == OrphanedOwner).Role);
        Assert.DoesNotContain(f.InventoryStore.Memberships, m => m.ParticipantId.ToString() == ActorId);
    }

    [Fact]
    public async Task Recovering_with_an_unresolvable_target_is_rejected()
    {
        var f = CreateFixture();

        var result = await f.Service.RecoverAsync(ActorId, f.OrphanedInventoryId, "unknown@example.com", Now, CancellationToken.None);

        Assert.Equal(RecoveryRequestOutcome.TargetNotResolved, result.Outcome);
    }

    [Fact]
    public async Task The_recovery_admin_actor_is_recorded_by_claim_value_not_as_a_participant_id()
    {
        var f = CreateFixture();

        await f.Service.RecoverAsync(ActorId, f.OrphanedInventoryId, Recovered.ToString(), Now, CancellationToken.None);

        var fact = Assert.Single(f.RecoveryStore.RecordedFacts);
        Assert.Equal(AuditActorKind.RecoveryAdministrator, fact.ActorKind);
        Assert.Equal(ActorId, fact.ActorId);
        Assert.Equal(AuditEventType.OrphanOwnershipRecovered, fact.EventType);
    }
}
