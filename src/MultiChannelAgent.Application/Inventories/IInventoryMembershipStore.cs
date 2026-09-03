using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum MembershipGrantOutcome
{
    Granted,
    RoleChanged,

    /// <summary>
    /// The resolved target already holds the current Owner Membership - granting/changing a role
    /// through this ordinary path can never touch the Owner; ownership transfer is the sole path.
    /// </summary>
    TargetIsOwner,
}

public sealed record MembershipGrantResult(MembershipGrantOutcome Outcome);

public enum MembershipRemovalOutcome
{
    Removed,

    /// <summary>The target holds no Membership on this Inventory - nothing to remove.</summary>
    NotAMember,

    /// <summary>The current Owner can never be removed through this ordinary path.</summary>
    TargetIsOwner,
}

public sealed record MembershipRemovalResult(MembershipRemovalOutcome Outcome);

/// <summary>One Inventory's current membership roster entry, as shown only to its Owner.</summary>
public sealed record MemberView(string ParticipantId, string DisplayName, string Role);

/// <summary>
/// The atomic seam for Owner-driven membership administration: every state change here - granting or
/// changing a Viewer/Editor role, or removing a non-Owner member - commits together with its semantic
/// audit fact and, for removal, the clearing of every Active Inventory selection the removed
/// Participant held for this Inventory, all in one transaction. Recipient acceptance is never
/// required: a grant takes effect immediately.
/// </summary>
public interface IInventoryMembershipStore
{
    Task<MembershipGrantResult> GrantOrChangeRoleAsync(
        InventoryId inventoryId,
        ParticipantId requesterId,
        ParticipantId targetParticipantId,
        string targetDisplayName,
        MembershipRole role,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<MembershipRemovalResult> RemoveAsync(
        InventoryId inventoryId,
        ParticipantId requesterId,
        ParticipantId targetParticipantId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>The full current membership roster - Owner-only, never exposed to non-Owners.</summary>
    Task<IReadOnlyList<MemberView>> ListMembersAsync(InventoryId inventoryId, CancellationToken cancellationToken);
}
