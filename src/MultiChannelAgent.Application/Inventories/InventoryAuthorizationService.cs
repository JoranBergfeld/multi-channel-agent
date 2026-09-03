using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

public enum InventoryAuthorizationOutcome
{
    /// <summary>The Participant holds a Membership that meets any required minimum role.</summary>
    Authorized,

    /// <summary>
    /// The Participant holds no Membership at all - whether the Inventory does not exist or simply
    /// is not authorized for them is indistinguishable by design.
    /// </summary>
    NotFound,

    /// <summary>The Participant holds a Membership, but not one privileged enough for the required role.</summary>
    Forbidden,
}

public sealed record InventoryAuthorizationResult(InventoryAuthorizationOutcome Outcome, MembershipRole? Role);

/// <summary>
/// The single seam every Inventory-scoped request - the selection endpoint today, and every later
/// Turn - must authorize through: it always rechecks current SQL Membership (never a stale
/// cookie/session role), clears the caller's Active Inventory selection immediately when access to it
/// is lost, and records a non-disclosing AccessDenied audit fact on every denial without ever
/// revealing to the caller whether the Inventory exists.
/// </summary>
public sealed class InventoryAuthorizationService(IInventoryStore inventoryStore, IInventoryAuthorizationAuditStore auditStore)
{
    public async Task<InventoryAuthorizationResult> AuthorizeAsync(
        ParticipantId participantId,
        InventoryId inventoryId,
        MembershipRole? requiredRole,
        string? channelConversationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var role = await inventoryStore.FindRoleAsync(inventoryId, participantId, cancellationToken);

        if (role is null)
        {
            var fact = AuditFact.Create(
                AuditEventType.AccessDenied,
                AuditActorKind.Participant,
                participantId.ToString(),
                inventoryId,
                participantId,
                "Denied:NotAMember",
                now);

            await auditStore.RecordDenialAsync(fact, participantId, channelConversationId, cancellationToken);
            return new InventoryAuthorizationResult(InventoryAuthorizationOutcome.NotFound, null);
        }

        if (requiredRole is not null && !InventoryGovernancePolicy.Satisfies(role.Value, requiredRole.Value))
        {
            var fact = AuditFact.Create(
                AuditEventType.AccessDenied,
                AuditActorKind.Participant,
                participantId.ToString(),
                inventoryId,
                participantId,
                "Denied:InsufficientRole",
                now);

            // A caller with an insufficient (but real) role still has access - there is no Active
            // Inventory selection to clear here, only Membership loss (the null-role branch above)
            // constitutes access loss.
            await auditStore.RecordDenialAsync(fact, null, null, cancellationToken);
            return new InventoryAuthorizationResult(InventoryAuthorizationOutcome.Forbidden, role);
        }

        return new InventoryAuthorizationResult(InventoryAuthorizationOutcome.Authorized, role);
    }
}
