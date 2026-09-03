using System.Security.Claims;

namespace MultiChannelAgent.Host.Authentication;

/// <summary>
/// Evaluates whether an authenticated Microsoft Entra ID-token principal carries explicit,
/// trustworthy evidence of being an active member of the configured single tenant. This fails closed:
/// the optional "acct" claim is only present when the app registration explicitly requests it, so its
/// absence (a common, valid configuration) must never be treated as "member" by default - that would
/// silently authorize guests and app registrations the tenant never confirmed as active members. Only
/// an "acct" claim literally equal to "0", together with a "tid" claim matching the configured tenant
/// and a present immutable "oid", counts as trusted evidence; every other combination - missing
/// "acct", a guest's non-"0" "acct", a mismatched "tid", or a missing "oid" - fails closed.
/// </summary>
public static class EntraTenantMembershipEvidence
{
    public static bool IsActiveTenantMember(ClaimsPrincipal principal, string configuredTenantId)
    {
        if (principal.FindFirst("acct")?.Value != "0")
        {
            return false;
        }

        var tenantId = principal.FindFirst("tid")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId) || !string.Equals(tenantId, configuredTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(principal.FindFirst("oid")?.Value);
    }
}
