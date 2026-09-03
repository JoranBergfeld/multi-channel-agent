using System.Security.Claims;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Host.Authentication;

/// <summary>
/// The claim shape shared by every authentication scheme (production Entra OIDC and the deterministic
/// test double): callers read a Participant's identity and authorization signal through these
/// constants instead of scheme-specific claim types, so downstream code never varies by provider.
/// </summary>
public static class ParticipantClaims
{
    /// <summary>The Participant's immutable Microsoft Entra object ID.</summary>
    public const string ParticipantId = "oid";

    /// <summary>The Participant's current display name.</summary>
    public const string DisplayName = "name";

    /// <summary>
    /// "true" only for an active tenant member the application trusts to act as a Participant;
    /// anything else (missing, "false") must be treated as a non-disclosing refusal.
    /// </summary>
    public const string ActiveTenantMember = "mca_active_tenant_member";

    /// <summary>
    /// The standard OIDC/Entra app-role claim type. Trusted authorization roles (for example
    /// <see cref="InventoryRecoveryAdministratorRoleValue"/>) are granted only from an explicit value
    /// of this claim - never inferred from display name or group membership.
    /// </summary>
    public const string AppRole = "roles";

    /// <summary>
    /// Grants the Inventory Recovery Administrator capability: transferring ownership of an orphaned
    /// Inventory only, with no access to stock and without ever becoming a member. Orthogonal to
    /// <see cref="ActiveTenantMember"/> - a Recovery Administrator need not be (and, per least
    /// privilege, ideally is not) an ordinary Participant.
    /// </summary>
    public const string InventoryRecoveryAdministratorRoleValue = "InventoryRecoveryAdministrator";
}

public static class ClaimsPrincipalExtensions
{
    public static ParticipantId GetParticipantId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirst(ParticipantClaims.ParticipantId)?.Value;
        if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var value))
        {
            throw new InvalidOperationException(
                $"Authenticated principal is missing a valid '{ParticipantClaims.ParticipantId}' claim.");
        }

        return new ParticipantId(value);
    }

    public static string GetDisplayName(this ClaimsPrincipal user) =>
        user.FindFirst(ParticipantClaims.DisplayName)?.Value ?? "Unknown Participant";

    /// <summary>
    /// A Recovery Administrator's trusted identity for audit purposes only - never a
    /// <see cref="ParticipantId"/>, since a Recovery Administrator is never a member. Falls back
    /// gracefully (never throws) because, unlike an ordinary Participant, a Recovery Administrator's
    /// claim shape is not guaranteed to carry a well-formed object id.
    /// </summary>
    public static string GetRecoveryActorId(this ClaimsPrincipal user) =>
        user.FindFirst(ParticipantClaims.ParticipantId)?.Value
        ?? user.FindFirst(ParticipantClaims.DisplayName)?.Value
        ?? "unknown-recovery-administrator";
}
