namespace MultiChannelAgent.Application.Inventories;

/// <summary>One authorized Inventory as shown to a Participant, with fields sufficient to disambiguate duplicates.</summary>
public sealed record InventoryView(string Id, string ShortId, string Name, string OwnerDisplayName, string Role);

/// <summary>The authenticated session bootstrap: canonical Participant, authorized Inventories, and Active Inventory.</summary>
public sealed record BootstrapView(
    string ParticipantId,
    string DisplayName,
    string WebConversationId,
    IReadOnlyList<InventoryView> Inventories,
    string? ActiveInventoryId,
    bool NeedsOnboarding);
