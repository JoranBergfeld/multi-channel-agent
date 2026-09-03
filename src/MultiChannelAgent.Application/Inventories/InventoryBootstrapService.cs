using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Composes the authenticated session bootstrap: ensures the canonical Participant, lists authorized
/// Inventories, and resolves the Active Inventory - auto-selecting only when exactly one Inventory is
/// accessible and nothing is already selected, per the "never guess with more than one" rule.
/// </summary>
public sealed class InventoryBootstrapService(
    ParticipantSessionService participantSessionService,
    InventoryListingService listingService,
    InventorySelectionService selectionService)
{
    public async Task<BootstrapView> BootstrapAsync(
        ParticipantId participantId,
        string displayName,
        string webConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var participant = await participantSessionService.EnsureParticipantAsync(participantId, displayName, cancellationToken);
        var inventories = await listingService.ListAuthorizedAsync(participantId, cancellationToken);
        var activeInventoryId = await selectionService.GetActiveInventoryIdAsync(participantId, webConversationId, now, cancellationToken);

        if (activeInventoryId is null && inventories.Count == 1)
        {
            var onlyInventoryId = new InventoryId(Guid.Parse(inventories[0].Id));
            var result = await selectionService.SelectAsync(participantId, onlyInventoryId, webConversationId, now, cancellationToken);
            if (result.Outcome == InventorySelectionOutcome.Selected)
            {
                activeInventoryId = result.InventoryId;
            }
        }

        return new BootstrapView(
            participant.Id.ToString(),
            participant.DisplayName,
            webConversationId,
            inventories,
            activeInventoryId?.ToString(),
            NeedsOnboarding: inventories.Count == 0);
    }
}
