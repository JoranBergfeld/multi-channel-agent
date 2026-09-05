namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// One Inventory's monotonic change version - the whole durable state behind "something you are
/// looking at changed, refetch it".
///
/// Deliberately a counter and not a timestamp, and deliberately without one: an invalidation signal
/// is compared for inequality, never for age, so a clock would only add a column that could disagree
/// with itself across replicas and would have to be threaded through a DbContext that has no
/// business knowing the time.
/// </summary>
public sealed class InventoryVersionEntity
{
    public Guid InventoryId { get; set; }

    public long Version { get; set; }
}
