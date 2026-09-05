namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Reads the current change version of Inventories. Versions are published by the persistence seam
/// itself, inside the very transaction that changes state, so there is deliberately no write method
/// here: nothing may ever bump a version without also making the change it announces.
/// </summary>
public interface IInventoryVersionStore
{
    /// <summary>
    /// The current version of each requested Inventory. An Inventory with no recorded version is
    /// simply absent from the result - callers treat that as version zero rather than as an error,
    /// because "never changed" and "changed zero times" are the same thing.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, long>> ReadAsync(
        IReadOnlyCollection<Guid> inventoryIds, CancellationToken cancellationToken);
}
