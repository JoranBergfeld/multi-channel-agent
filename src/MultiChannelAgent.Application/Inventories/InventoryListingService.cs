using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Lists only the Inventories a Participant is authorized for - never anything they are not a member
/// of - in a stable, deterministic order (normalized name, then stable short identifier) so duplicate
/// names remain distinguishable and paging/UI ordering never depends on database row order.
/// </summary>
public sealed class InventoryListingService(IInventoryStore store)
{
    public async Task<IReadOnlyList<InventoryView>> ListAuthorizedAsync(ParticipantId participantId, CancellationToken cancellationToken)
    {
        var records = await store.ListAuthorizedAsync(participantId, cancellationToken);

        return records
            .OrderBy(r => NameNormalization.Normalize(r.Name), StringComparer.Ordinal)
            .ThenBy(r => r.InventoryId.ShortId, StringComparer.Ordinal)
            .Select(r => new InventoryView(
                r.InventoryId.ToString(),
                r.InventoryId.ShortId,
                r.Name,
                r.OwnerDisplayName,
                r.Role.ToString()))
            .ToList();
    }
}
