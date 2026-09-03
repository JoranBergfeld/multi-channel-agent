using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>Durable store for canonical Participants, upserted idempotently from authenticated claims.</summary>
public interface IParticipantStore
{
    Task UpsertAsync(Participant participant, CancellationToken cancellationToken);
}

/// <summary>Result of an idempotent Inventory creation attempt.</summary>
public sealed record InventoryCreationResult(Inventory Inventory, bool WasAlreadyCreated);

/// <summary>
/// One authorized Inventory for a Participant, including the Owner's display name so duplicate
/// Inventory names remain distinguishable in a view without guessing.
/// </summary>
public sealed record AuthorizedInventoryRecord(
    InventoryId InventoryId,
    string Name,
    ParticipantId OwnerParticipantId,
    string OwnerDisplayName,
    MembershipRole Role);

/// <summary>
/// The sole authority for Inventory creation, role lookup, and authorized listing. Creation is
/// atomic: the Inventory row, its Owner Membership, and its reserved `each` Unit (with fixed aliases)
/// must commit together in one operation, and a duplicate <see cref="Inventory.ClientRequestId"/> from
/// the same creator must never create a second Inventory.
/// </summary>
public interface IInventoryStore
{
    Task<Inventory?> FindByClientRequestIdAsync(ParticipantId createdBy, string clientRequestId, CancellationToken cancellationToken);

    Task<InventoryCreationResult> CreateAsync(Inventory inventory, Unit reservedEachUnit, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the Participant's role for the given Inventory, or null when the Inventory does not
    /// exist or the Participant has no Membership - callers must treat both cases identically so
    /// unauthorized Inventories never become discoverable.
    /// </summary>
    Task<MembershipRole?> FindRoleAsync(InventoryId inventoryId, ParticipantId participantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<AuthorizedInventoryRecord>> ListAuthorizedAsync(ParticipantId participantId, CancellationToken cancellationToken);
}

/// <summary>Durable store for one Participant's Active Inventory selection per ChannelConversation.</summary>
public interface IActiveInventorySelectionStore
{
    Task<ActiveInventorySelection?> FindAsync(ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken);

    Task UpsertAsync(ActiveInventorySelection selection, CancellationToken cancellationToken);

    Task ClearAsync(ParticipantId participantId, string channelConversationId, CancellationToken cancellationToken);
}
