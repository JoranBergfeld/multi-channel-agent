using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

public class InventoryListingServiceTests
{
    private static readonly ParticipantId Alice = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Bob = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly ParticipantId Carol = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<ParticipantId, string> DisplayNames = new()
    {
        [Alice] = "Alice Owner",
        [Bob] = "Bob Owner",
        [Carol] = "Carol NonMember",
    };

    [Fact]
    public async Task Listing_returns_only_inventories_the_participant_is_a_member_of()
    {
        var store = new InMemoryInventoryStore(id => DisplayNames[id]);
        var creation = new InventoryCreationService(store);
        var listing = new InventoryListingService(store);

        var mine = await creation.CreateAsync(Alice, "Alice Owner", "Mine", "req-1", Now, CancellationToken.None);
        await creation.CreateAsync(Bob, "Bob Owner", "NotMine", "req-2", Now, CancellationToken.None);

        var results = await listing.ListAuthorizedAsync(Alice, CancellationToken.None);

        var view = Assert.Single(results);
        Assert.Equal(mine.Id, view.Id);
        Assert.Equal("Alice Owner", view.OwnerDisplayName);
    }

    // A Participant with no Membership at all - including one who has never interacted with the
    // system - must get an empty authorized list, never an error or leaked existence signal.
    [Fact]
    public async Task Listing_returns_empty_for_a_participant_with_no_memberships()
    {
        var store = new InMemoryInventoryStore(id => DisplayNames[id]);
        var listing = new InventoryListingService(store);

        var results = await listing.ListAuthorizedAsync(Carol, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Listing_orders_results_deterministically_by_normalized_name_then_short_id()
    {
        var store = new InMemoryInventoryStore(id => DisplayNames[id]);
        var creation = new InventoryCreationService(store);
        var listing = new InventoryListingService(store);

        await creation.CreateAsync(Alice, "Alice Owner", "Zebra Stock", "req-1", Now, CancellationToken.None);
        await creation.CreateAsync(Alice, "Alice Owner", "  aardvark stock  ", "req-2", Now, CancellationToken.None);
        await creation.CreateAsync(Alice, "Alice Owner", "Middle Stock", "req-3", Now, CancellationToken.None);

        var results = await listing.ListAuthorizedAsync(Alice, CancellationToken.None);

        Assert.Equal(["aardvark stock", "Middle Stock", "Zebra Stock"], results.Select(r => r.Name));
    }

    // Duplicate names must remain distinguishable via Owner display name + stable short identifier,
    // never guessed away - both fields must be present and distinct for colliding names.
    [Fact]
    public async Task Duplicate_names_are_distinguished_by_owner_display_name_and_short_id()
    {
        var store = new InMemoryInventoryStore(id => DisplayNames[id]);
        var creation = new InventoryCreationService(store);
        var listing = new InventoryListingService(store);
        var participantId = new ParticipantId(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        var namesById = new Dictionary<ParticipantId, string>(DisplayNames) { [participantId] = "Dave Owner" };
        var multiOwnerStore = new InMemoryInventoryStore(id => namesById[id]);
        var multiOwnerCreation = new InventoryCreationService(multiOwnerStore);
        var multiOwnerListing = new InventoryListingService(multiOwnerStore);

        await multiOwnerCreation.CreateAsync(Alice, "Alice Owner", "Warehouse", "req-1", Now, CancellationToken.None);
        await multiOwnerCreation.CreateAsync(participantId, "Dave Owner", "Warehouse", "req-2", Now, CancellationToken.None);

        // Grant Alice access to Dave's "Warehouse" too, so both collide in her authorized list.
        var daveInventory = multiOwnerStore.Inventories.Single(i => i.CreatedByParticipantId == participantId);
        multiOwnerStore.GrantMembership(daveInventory.Id, Alice, MembershipRole.Viewer, Now);

        var results = await multiOwnerListing.ListAuthorizedAsync(Alice, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal("Warehouse", r.Name));
        Assert.Equal(2, results.Select(r => (r.OwnerDisplayName, r.ShortId)).Distinct().Count());
    }
}
