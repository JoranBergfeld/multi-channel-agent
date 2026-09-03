using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum TransferOutcome
{
    Transferred,

    /// <summary>The requester is no longer the current Owner at commit time (defense in depth against a concurrent transfer).</summary>
    RequesterNotOwner,

    /// <summary>Another transfer committed for this Inventory between this request's read and its write.</summary>
    ConcurrentModification,
}

public sealed record TransferResult(TransferOutcome Outcome);

/// <summary>
/// The atomic seam for ownership transfer: promotes the target to Owner and demotes the previous
/// Owner to Editor (preserving their access) in one transaction, guarded by optimistic concurrency on
/// the Owner Membership row so two concurrent transfer attempts can never both succeed - an Inventory
/// can never intentionally become ownerless.
/// </summary>
public interface IInventoryOwnershipStore
{
    Task<TransferResult> TransferAsync(
        InventoryId inventoryId,
        ParticipantId requesterId,
        ParticipantId targetParticipantId,
        string targetDisplayName,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
