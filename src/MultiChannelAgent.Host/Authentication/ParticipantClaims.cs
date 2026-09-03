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
}
