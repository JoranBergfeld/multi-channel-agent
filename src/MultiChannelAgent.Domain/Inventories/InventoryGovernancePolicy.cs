namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// Pure governance rules shared by every membership/ownership operation: the ordinary grant/change
/// role matrix, self-transfer rejection, the orphan predicate, and the Owner-hierarchy role check
/// used to authorize a request against a minimum required role. None of these depend on a store -
/// they are the same invariants regardless of how the caller/target were resolved.
/// </summary>
public static class InventoryGovernancePolicy
{
    /// <summary>
    /// Only Viewer and Editor may be granted through the ordinary membership grant/change endpoint;
    /// Owner is reachable only through ownership transfer, never through an ordinary role change.
    /// </summary>
    public static bool IsGrantableRole(MembershipRole role) => role is MembershipRole.Viewer or MembershipRole.Editor;

    /// <summary>Ownership transfer to the current Owner themselves is a no-op/conflict, not a valid transfer.</summary>
    public static bool IsSelfTransfer(ParticipantId currentOwnerId, ParticipantId targetParticipantId) =>
        currentOwnerId == targetParticipantId;

    /// <summary>
    /// An Inventory is orphaned exactly when its sole Owner Participant is no longer active/resolvable
    /// per the tenant directory - never merely because a Membership row happens to be missing.
    /// </summary>
    public static bool IsOrphaned(bool ownerIsActive) => !ownerIsActive;

    /// <summary>
    /// True when <paramref name="actualRole"/> meets or exceeds <paramref name="requiredRole"/> in the
    /// Owner &gt; Editor &gt; Viewer privilege hierarchy (the enum's declaration order - Owner = 0 - is
    /// deliberately the most-privileged value first, so a lower numeric value always satisfies a
    /// higher one).
    /// </summary>
    public static bool Satisfies(MembershipRole actualRole, MembershipRole requiredRole) => actualRole <= requiredRole;

    /// <summary>
    /// The current Owner can never be removed through the ordinary membership-removal endpoint;
    /// ownership transfer is the sole path that ever changes who the Owner is.
    /// </summary>
    public static bool CanRemoveMember(MembershipRole targetRole) => targetRole != MembershipRole.Owner;
}
