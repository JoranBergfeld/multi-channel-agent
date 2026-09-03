using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum MembershipRequestOutcome
{
    Granted,
    RoleChanged,
    Removed,

    /// <summary>The requester holds no Membership at all - non-disclosing, identical to a nonexistent Inventory.</summary>
    RequesterNotAuthorized,

    /// <summary>The requester is a real member but not the Owner.</summary>
    RequesterNotOwner,

    /// <summary>The requested role is not grantable through this ordinary path (only Viewer/Editor are).</summary>
    InvalidRole,

    /// <summary>The target identifier did not resolve to an exact, active, non-guest tenant member.</summary>
    TargetNotResolved,

    /// <summary>The resolved target already holds the Owner Membership - use ownership transfer instead.</summary>
    TargetIsOwner,

    /// <summary>Nothing to remove: the target holds no Membership on this Inventory.</summary>
    TargetNotAMember,

    /// <summary>A concurrent ownership transfer or recovery committed against this same Membership row between this request's read and its write; retry.</summary>
    ConcurrentModification,
}

public sealed record MembershipRequestResult(MembershipRequestOutcome Outcome);

public enum MembershipListOutcome
{
    Listed,
    RequesterNotAuthorized,
    RequesterNotOwner,
}

public sealed record MembershipListResult(MembershipListOutcome Outcome, IReadOnlyList<MemberView>? Members);

/// <summary>
/// Owner-only membership governance: granting or changing a Viewer/Editor role for a resolvable
/// active tenant member (recipient acceptance is never required), and removing a non-Owner member.
/// Every requester check flows through <see cref="InventoryAuthorizationService"/>, so a non-member
/// requester and a non-owner member requester are both refused, and the Owner can never remove or
/// demote themselves through this path - only ownership transfer changes who the Owner is.
/// </summary>
public sealed class InventoryMembershipService(
    InventoryAuthorizationService authorizationService,
    ITenantMemberDirectory directory,
    IParticipantStore participantStore,
    IInventoryMembershipStore membershipStore)
{
    public async Task<MembershipRequestResult> GrantOrChangeAsync(
        ParticipantId requesterId,
        InventoryId inventoryId,
        string? targetIdentifierRaw,
        MembershipRole requestedRole,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ownerCheck = await RequireOwnerAsync(requesterId, inventoryId, now, cancellationToken);
        if (ownerCheck is not null)
        {
            return new MembershipRequestResult(ownerCheck.Value);
        }

        if (!InventoryGovernancePolicy.IsGrantableRole(requestedRole))
        {
            return new MembershipRequestResult(MembershipRequestOutcome.InvalidRole);
        }

        var identifier = TenantMemberIdentifier.Parse(targetIdentifierRaw);
        if (identifier is null)
        {
            return new MembershipRequestResult(MembershipRequestOutcome.TargetNotResolved);
        }

        var resolved = await directory.ResolveAsync(identifier, cancellationToken);
        if (resolved is null)
        {
            return new MembershipRequestResult(MembershipRequestOutcome.TargetNotResolved);
        }

        await participantStore.UpsertAsync(Participant.Create(resolved.ParticipantId, resolved.DisplayName), cancellationToken);

        var result = await membershipStore.GrantOrChangeRoleAsync(
            inventoryId, requesterId, resolved.ParticipantId, resolved.DisplayName, requestedRole, now, cancellationToken);

        return new MembershipRequestResult(result.Outcome switch
        {
            MembershipGrantOutcome.Granted => MembershipRequestOutcome.Granted,
            MembershipGrantOutcome.RoleChanged => MembershipRequestOutcome.RoleChanged,
            MembershipGrantOutcome.TargetIsOwner => MembershipRequestOutcome.TargetIsOwner,
            MembershipGrantOutcome.ConcurrentModification => MembershipRequestOutcome.ConcurrentModification,
            _ => throw new InvalidOperationException($"Unhandled {nameof(MembershipGrantOutcome)}: {result.Outcome}"),
        });
    }

    public async Task<MembershipRequestResult> RemoveAsync(
        ParticipantId requesterId,
        InventoryId inventoryId,
        ParticipantId targetParticipantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ownerCheck = await RequireOwnerAsync(requesterId, inventoryId, now, cancellationToken);
        if (ownerCheck is not null)
        {
            return new MembershipRequestResult(ownerCheck.Value);
        }

        var result = await membershipStore.RemoveAsync(inventoryId, requesterId, targetParticipantId, now, cancellationToken);

        return new MembershipRequestResult(result.Outcome switch
        {
            MembershipRemovalOutcome.Removed => MembershipRequestOutcome.Removed,
            MembershipRemovalOutcome.NotAMember => MembershipRequestOutcome.TargetNotAMember,
            MembershipRemovalOutcome.TargetIsOwner => MembershipRequestOutcome.TargetIsOwner,
            MembershipRemovalOutcome.ConcurrentModification => MembershipRequestOutcome.ConcurrentModification,
            _ => throw new InvalidOperationException($"Unhandled {nameof(MembershipRemovalOutcome)}: {result.Outcome}"),
        });
    }

    public async Task<MembershipListResult> ListMembersAsync(
        ParticipantId requesterId, InventoryId inventoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ownerCheck = await RequireOwnerAsync(requesterId, inventoryId, now, cancellationToken);
        if (ownerCheck is not null)
        {
            return new MembershipListResult(ownerCheck.Value switch
            {
                MembershipRequestOutcome.RequesterNotAuthorized => MembershipListOutcome.RequesterNotAuthorized,
                _ => MembershipListOutcome.RequesterNotOwner,
            }, null);
        }

        var members = await membershipStore.ListMembersAsync(inventoryId, cancellationToken);
        return new MembershipListResult(MembershipListOutcome.Listed, members);
    }

    private async Task<MembershipRequestOutcome?> RequireOwnerAsync(
        ParticipantId requesterId, InventoryId inventoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            requesterId, inventoryId, MembershipRole.Owner, channelConversationId: null, now, cancellationToken);

        return authorization.Outcome switch
        {
            InventoryAuthorizationOutcome.Authorized => null,
            InventoryAuthorizationOutcome.NotFound => MembershipRequestOutcome.RequesterNotAuthorized,
            InventoryAuthorizationOutcome.Forbidden => MembershipRequestOutcome.RequesterNotOwner,
            _ => throw new InvalidOperationException($"Unhandled {nameof(InventoryAuthorizationOutcome)}: {authorization.Outcome}"),
        };
    }
}
