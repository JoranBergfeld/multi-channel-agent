using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Tests.Inventories;

public class ActiveInventorySelectionTests
{
    private static readonly ParticipantId SomeParticipant = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly InventoryId SomeInventory = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public void IsExpired_is_false_before_thirty_inactive_days_have_elapsed()
    {
        var lastActivityAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var selection = new ActiveInventorySelection(SomeParticipant, "web-conv-1", SomeInventory, lastActivityAt);

        Assert.False(selection.IsExpired(lastActivityAt + TimeSpan.FromDays(29)));
    }

    [Fact]
    public void IsExpired_is_true_once_more_than_thirty_inactive_days_have_elapsed()
    {
        var lastActivityAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var selection = new ActiveInventorySelection(SomeParticipant, "web-conv-1", SomeInventory, lastActivityAt);

        Assert.True(selection.IsExpired(lastActivityAt + TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1)));
    }
}
