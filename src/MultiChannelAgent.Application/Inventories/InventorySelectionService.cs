using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum InventorySelectionOutcome
{
    Selected,
    NotAuthorized,
}

public sealed record InventorySelectionResult(InventorySelectionOutcome Outcome, InventoryId? InventoryId);

/// <summary>
/// Selects the Active Inventory for one Participant/ChannelConversation and reads it back. Selection
/// always rechecks Membership - it never grants access by itself - and a Participant who is not (or
/// no longer) a member gets the same non-disclosing outcome whether the Inventory exists or not.
/// </summary>
public sealed class InventorySelectionService(IInventoryStore inventoryStore, IActiveInventorySelectionStore selectionStore)
{
    public async Task<InventorySelectionResult> SelectAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var role = await inventoryStore.FindRoleAsync(inventoryId, participantId, cancellationToken);
        if (role is null)
        {
            return new InventorySelectionResult(InventorySelectionOutcome.NotAuthorized, null);
        }

        await selectionStore.UpsertAsync(
            new ActiveInventorySelection(participantId, channelConversationId, inventoryId, now),
            cancellationToken);

        return new InventorySelectionResult(InventorySelectionOutcome.Selected, inventoryId);
    }

    /// <summary>
    /// Returns the current Active Inventory for this Participant/ChannelConversation, or null when
    /// none is selected, the selection has expired after 30 inactive days, or Membership has since
    /// been revoked (access loss) - clearing the stale selection in the latter two cases.
    /// </summary>
    public async Task<InventoryId?> GetActiveInventoryIdAsync(
        ParticipantId participantId,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await selectionStore.FindAsync(participantId, channelConversationId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (existing.IsExpired(now))
        {
            await selectionStore.ClearAsync(participantId, channelConversationId, cancellationToken);
            return null;
        }

        var role = await inventoryStore.FindRoleAsync(existing.InventoryId, participantId, cancellationToken);
        if (role is null)
        {
            await selectionStore.ClearAsync(participantId, channelConversationId, cancellationToken);
            return null;
        }

        return existing.InventoryId;
    }
}
