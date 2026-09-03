using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum InventorySelectionOutcome
{
    Selected,
    NotAuthorized,
}

public sealed record InventorySelectionResult(InventorySelectionOutcome Outcome, InventoryId? InventoryId);

/// <summary>
/// Selects the Active Inventory for one Participant/ChannelConversation and reads it back. Every
/// authorization check flows through <see cref="InventoryAuthorizationService"/> - the single seam
/// that always rechecks current SQL Membership, records a non-disclosing AccessDenied audit fact on
/// denial, and clears the stale selection on access loss - so selection never grants access by
/// itself, and a Participant who is not (or no longer) a member gets the same non-disclosing outcome
/// whether the Inventory exists or not.
/// </summary>
public sealed class InventorySelectionService(InventoryAuthorizationService authorizationService, IActiveInventorySelectionStore selectionStore)
{
    public async Task<InventorySelectionResult> SelectAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        string channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            participantId, inventoryId, requiredRole: null, channelConversationId, now, cancellationToken);
        if (authorization.Outcome != InventoryAuthorizationOutcome.Authorized)
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

        var authorization = await authorizationService.AuthorizeAsync(
            participantId, existing.InventoryId, requiredRole: null, channelConversationId, now, cancellationToken);

        return authorization.Outcome == InventoryAuthorizationOutcome.Authorized ? existing.InventoryId : null;
    }
}
