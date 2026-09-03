using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Explicitly creates a named Inventory and atomically makes the requester its Owner, with the
/// reserved `each` Unit and its fixed aliases created in the same operation. Idempotent by the
/// caller-supplied <see cref="Inventory.ClientRequestId"/>: resubmitting the same (requester,
/// ClientRequestId) pair - including two concurrent deliveries of it - returns the original Inventory
/// instead of creating another.
/// </summary>
public sealed class InventoryCreationService(IInventoryStore store)
{
    public async Task<InventoryView> CreateAsync(
        ParticipantId requester,
        string requesterDisplayName,
        string name,
        string clientRequestId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await store.FindByClientRequestIdAsync(requester, clientRequestId, cancellationToken);
        if (existing is not null)
        {
            return ToView(existing, requesterDisplayName);
        }

        var inventory = Inventory.Create(name, requester, clientRequestId, now);
        var reservedEachUnit = Unit.CreateReservedEach(inventory.Id, now);

        var result = await store.CreateAsync(inventory, reservedEachUnit, cancellationToken);

        return ToView(result.Inventory, requesterDisplayName);
    }

    private static InventoryView ToView(Inventory inventory, string ownerDisplayName) => new(
        inventory.Id.ToString(),
        inventory.Id.ShortId,
        inventory.Name,
        ownerDisplayName,
        nameof(MembershipRole.Owner));
}
