using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Application.Tests.TestDoubles.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Tests.Inventories;

/// <summary>
/// The whole payload of the Participant-level invalidation stream: which Inventories this Participant
/// may currently see, and what version each is at. Because the stream sends this complete picture on
/// every connection, every reconnect is a total resynchronization - which is why it needs no cursor
/// and can never miss a change that happened while a tab was closed.
/// </summary>
public class InventoryInvalidationReaderTests
{
    private static readonly ParticipantId Participant = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly ParticipantId Owner = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly InMemoryInventoryStore _inventories = new(_ => "Any Owner");
    private readonly InMemoryInventoryVersionStore _versions = new();

    private InventoryInvalidationReader Reader => new(_inventories, _versions);

    [Fact]
    public async Task A_participant_with_no_memberships_is_told_about_nothing()
    {
        Assert.Empty(await Reader.ReadAsync(Participant, CancellationToken.None));
    }

    [Fact]
    public async Task Only_authorized_inventories_are_reported()
    {
        var mine = await CreateAsync("Mine", Participant);
        var theirs = await CreateAsync("Theirs", Owner);
        _versions.Set(mine, 7L);
        _versions.Set(theirs, 99L);

        var reported = await Reader.ReadAsync(Participant, CancellationToken.None);

        var only = Assert.Single(reported);
        Assert.Equal(mine, only.InventoryId);
        Assert.Equal(7L, only.Version);
    }

    [Fact]
    public async Task An_inventory_with_no_recorded_version_reports_as_never_changed()
    {
        var mine = await CreateAsync("Mine", Participant);

        var only = Assert.Single(await Reader.ReadAsync(Participant, CancellationToken.None));

        Assert.Equal(mine, only.InventoryId);
        Assert.Equal(0L, only.Version);
    }

    [Fact]
    public async Task The_report_is_ordered_stably_so_two_reads_of_the_same_state_are_identical()
    {
        for (var i = 0; i < 5; i++)
        {
            await CreateAsync($"Warehouse {i}", Participant);
        }

        var first = await Reader.ReadAsync(Participant, CancellationToken.None);
        var second = await Reader.ReadAsync(Participant, CancellationToken.None);

        Assert.Equal(first.Select(v => v.InventoryId), second.Select(v => v.InventoryId));
        Assert.Equal(first.Select(v => v.InventoryId).Order(), first.Select(v => v.InventoryId));
    }

    private async Task<Guid> CreateAsync(string name, ParticipantId owner)
    {
        var inventory = Inventory.Create(name, owner, Guid.NewGuid().ToString(), DateTimeOffset.UnixEpoch);
        await _inventories.CreateAsync(
            inventory, Unit.CreateReservedEach(inventory.Id, DateTimeOffset.UnixEpoch), CancellationToken.None);
        return inventory.Id.Value;
    }
}
