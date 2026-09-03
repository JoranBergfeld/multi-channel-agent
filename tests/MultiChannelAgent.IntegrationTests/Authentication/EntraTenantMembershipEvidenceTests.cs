using System.Security.Claims;
using MultiChannelAgent.Host.Authentication;

namespace MultiChannelAgent.IntegrationTests.Authentication;

/// <summary>
/// Unit coverage for the Entra ID-token evidence rule behind <see
/// cref="ParticipantClaims.ActiveTenantMember"/>: only an explicit, trustworthy signal (an "acct"
/// claim literally equal to "0", a "tid" claim matching the configured tenant, and a present "oid")
/// may grant active-tenant-member status. Anything else - no "acct" claim at all, a guest's non-"0"
/// "acct", a mismatched "tid", or a missing "oid" - must fail closed rather than defaulting to
/// "member", so a misconfigured or under-specified token can never silently authorize a guest or an
/// app registration.
/// </summary>
public sealed class EntraTenantMembershipEvidenceTests
{
    private const string ConfiguredTenantId = "11111111-1111-1111-1111-111111111111";

    private static ClaimsPrincipal PrincipalWith(string? acct, string? tid, string? oid)
    {
        var claims = new List<Claim>();
        if (acct is not null)
        {
            claims.Add(new Claim("acct", acct));
        }

        if (tid is not null)
        {
            claims.Add(new Claim("tid", tid));
        }

        if (oid is not null)
        {
            claims.Add(new Claim("oid", oid));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims));
    }

    [Fact]
    public void An_active_member_with_explicit_trusted_evidence_is_recognized()
    {
        var principal = PrincipalWith(acct: "0", tid: ConfiguredTenantId, oid: Guid.NewGuid().ToString());

        Assert.True(EntraTenantMembershipEvidence.IsActiveTenantMember(principal, ConfiguredTenantId));
    }

    [Fact]
    public void A_guest_with_a_nonzero_acct_claim_fails_closed()
    {
        var principal = PrincipalWith(acct: "1", tid: ConfiguredTenantId, oid: Guid.NewGuid().ToString());

        Assert.False(EntraTenantMembershipEvidence.IsActiveTenantMember(principal, ConfiguredTenantId));
    }

    [Fact]
    public void A_missing_acct_claim_fails_closed_instead_of_defaulting_to_member()
    {
        var principal = PrincipalWith(acct: null, tid: ConfiguredTenantId, oid: Guid.NewGuid().ToString());

        Assert.False(EntraTenantMembershipEvidence.IsActiveTenantMember(principal, ConfiguredTenantId));
    }

    [Fact]
    public void A_mismatched_tid_claim_fails_closed()
    {
        var principal = PrincipalWith(acct: "0", tid: "22222222-2222-2222-2222-222222222222", oid: Guid.NewGuid().ToString());

        Assert.False(EntraTenantMembershipEvidence.IsActiveTenantMember(principal, ConfiguredTenantId));
    }

    [Fact]
    public void A_missing_oid_claim_fails_closed()
    {
        var principal = PrincipalWith(acct: "0", tid: ConfiguredTenantId, oid: null);

        Assert.False(EntraTenantMembershipEvidence.IsActiveTenantMember(principal, ConfiguredTenantId));
    }
}
