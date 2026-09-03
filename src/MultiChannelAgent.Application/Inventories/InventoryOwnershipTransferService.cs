using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum TransferRequestOutcome
{
    Transferred,
    RequesterNotAuthorized,
    RequesterNotOwner,

    /// <summary>Transferring ownership to the current Owner themselves is a typed no-op/conflict, not a valid transfer.</summary>
    SelfTransferRejected,

    TargetNotResolved,
    ConcurrentModification,
}

public sealed record TransferRequestResult(TransferRequestOutcome Outcome);

/// <summary>
/// Owner-only ownership transfer to an existing, resolvable, active tenant member. Rejects
/// self-transfer as a typed conflict rather than a silent no-op, and the underlying
/// <see cref="IInventoryOwnershipStore"/> guards the actual state change with optimistic concurrency
/// so an Inventory can never intentionally become ownerless.
/// </summary>
public sealed class InventoryOwnershipTransferService(
    InventoryAuthorizationService authorizationService,
    ITenantMemberDirectory directory,
    IParticipantStore participantStore,
    IInventoryOwnershipStore ownershipStore)
{
    public async Task<TransferRequestResult> TransferAsync(
        ParticipantId requesterId,
        InventoryId inventoryId,
        string? targetIdentifierRaw,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizationService.AuthorizeAsync(
            requesterId, inventoryId, MembershipRole.Owner, channelConversationId: null, now, cancellationToken);

        switch (authorization.Outcome)
        {
            case InventoryAuthorizationOutcome.NotFound:
                return new TransferRequestResult(TransferRequestOutcome.RequesterNotAuthorized);
            case InventoryAuthorizationOutcome.Forbidden:
                return new TransferRequestResult(TransferRequestOutcome.RequesterNotOwner);
        }

        var identifier = TenantMemberIdentifier.Parse(targetIdentifierRaw);
        if (identifier is null)
        {
            return new TransferRequestResult(TransferRequestOutcome.TargetNotResolved);
        }

        var resolved = await directory.ResolveAsync(identifier, cancellationToken);
        if (resolved is null)
        {
            return new TransferRequestResult(TransferRequestOutcome.TargetNotResolved);
        }

        if (InventoryGovernancePolicy.IsSelfTransfer(requesterId, resolved.ParticipantId))
        {
            return new TransferRequestResult(TransferRequestOutcome.SelfTransferRejected);
        }

        await participantStore.UpsertAsync(Participant.Create(resolved.ParticipantId, resolved.DisplayName), cancellationToken);

        var result = await ownershipStore.TransferAsync(
            inventoryId, requesterId, resolved.ParticipantId, resolved.DisplayName, now, cancellationToken);

        return new TransferRequestResult(result.Outcome switch
        {
            TransferOutcome.Transferred => TransferRequestOutcome.Transferred,
            TransferOutcome.RequesterNotOwner => TransferRequestOutcome.RequesterNotOwner,
            TransferOutcome.ConcurrentModification => TransferRequestOutcome.ConcurrentModification,
            _ => throw new InvalidOperationException($"Unhandled {nameof(TransferOutcome)}: {result.Outcome}"),
        });
    }
}
