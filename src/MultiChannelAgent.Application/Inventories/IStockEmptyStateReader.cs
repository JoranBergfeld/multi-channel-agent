using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// The single authorized read behind Initial Import's eligibility gate: does this Inventory hold any
/// Stock Entry at all?
///
/// "At all" is the whole point. #34 says "including zero-quantity entries", so this deliberately does
/// not filter on Quantity: a zero-quantity Stock Entry is a Stock Entry, which is exactly why the
/// conversational Forget exists to remove one.
/// </summary>
public interface IStockEmptyStateReader
{
    Task<bool> AnyStockAsync(InventoryId inventoryId, CancellationToken cancellationToken);
}
