using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryCreationServiceTests
{
    private static readonly ParticipantId Requester = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (InventoryCreationService Service, InMemoryInventoryStore Store) CreateService(
        string requesterDisplayName = "Ada Lovelace")
    {
        var store = new InMemoryInventoryStore(_ => requesterDisplayName);
        return (new InventoryCreationService(store), store);
    }

    [Fact]
    public async Task Creating_an_inventory_atomically_makes_the_requester_owner_with_the_reserved_each_unit()
    {
        var (service, store) = CreateService();

        var view = await service.CreateAsync(Requester, "Ada Lovelace", "Warehouse", "req-1", Now, CancellationToken.None);

        Assert.Equal("Warehouse", view.Name);
        Assert.Equal("Owner", view.Role);
        Assert.Equal("Ada Lovelace", view.OwnerDisplayName);
        Assert.Equal(8, view.ShortId.Length);

        var inventory = Assert.Single(store.Inventories);
        Assert.Equal(Requester, inventory.CreatedByParticipantId);

        var membership = Assert.Single(store.Memberships);
        Assert.Equal(MembershipRole.Owner, membership.Role);
        Assert.Equal(Requester, membership.ParticipantId);

        var reservedUnit = store.ReservedEachUnits[inventory.Id];
        Assert.Equal("each", reservedUnit.CanonicalName);
        Assert.True(reservedUnit.IsReserved);
        Assert.Equal(["piece", "pieces", "pc", "pcs"], reservedUnit.Aliases);
    }

    [Fact]
    public async Task Resubmitting_the_same_client_request_id_returns_the_original_inventory_without_creating_another()
    {
        var (service, store) = CreateService();

        var first = await service.CreateAsync(Requester, "Ada Lovelace", "Warehouse", "req-1", Now, CancellationToken.None);
        var second = await service.CreateAsync(Requester, "Ada Lovelace", "Warehouse", "req-1", Now.AddMinutes(5), CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(store.Inventories);
        Assert.Single(store.Memberships);
        Assert.Single(store.ReservedEachUnits);
    }

    [Fact]
    public async Task A_different_client_request_id_creates_a_distinct_inventory()
    {
        var (service, store) = CreateService();

        var first = await service.CreateAsync(Requester, "Ada Lovelace", "Warehouse", "req-1", Now, CancellationToken.None);
        var second = await service.CreateAsync(Requester, "Ada Lovelace", "Warehouse", "req-2", Now, CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, store.Inventories.Count);
    }

    // Two concurrent deliveries of the same idempotent create request (e.g. a client retry racing
    // its own original request) must converge on exactly one Inventory rather than each independently
    // observing "not yet created" and both proceeding to create one.
    [Fact]
    public async Task Two_concurrent_creation_attempts_with_the_same_client_request_id_converge_on_one_inventory()
    {
        var store = new InMemoryInventoryStore(_ => "Ada Lovelace");
        var service = new InventoryCreationService(store);

        var first = service.CreateAsync(Requester, "Ada Lovelace", "Warehouse", "req-race", Now, CancellationToken.None);
        var second = service.CreateAsync(Requester, "Ada Lovelace", "Warehouse", "req-race", Now, CancellationToken.None);

        var results = await Task.WhenAll(first, second);

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Single(store.Inventories);
    }
}
