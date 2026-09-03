using System.Collections.Concurrent;
using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Host.Authentication;

/// <summary>
/// The deterministic <see cref="ITenantMemberDirectory"/> stand-in used only when
/// <c>Authentication:Provider=Test</c> - the same environment-gated substitution
/// <see cref="TestChallengeAuthenticationHandler"/> and <c>/api/test/sign-in</c> already use. Tests
/// register exactly the identities they want resolvable (via <c>/api/test/sign-in</c>, which
/// auto-registers the signed-in identity, or the dedicated
/// <c>/api/test/tenant-directory/register</c> endpoint for identities that have not signed in
/// themselves) - anything unregistered resolves to null, exactly like a real inactive/guest/unknown
/// identity would.
/// </summary>
public sealed class TestTenantMemberDirectory : ITenantMemberDirectory
{
    private readonly ConcurrentDictionary<Guid, ResolvedTenantMember> _byObjectId = new();
    private readonly ConcurrentDictionary<string, ResolvedTenantMember> _byAddress = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ResolvedTenantMember member) => _byObjectId[member.ParticipantId.Value] = member;

    public void Register(string address, ResolvedTenantMember member) => _byAddress[address] = member;

    /// <summary>Removes a previously registered identity - simulating a tenant member who has since left/been disabled, without a real Microsoft Graph call.</summary>
    public void Unregister(Guid objectId) => _byObjectId.TryRemove(objectId, out _);

    public Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken)
    {
        if (identifier.ObjectId is { } objectId)
        {
            return Task.FromResult(_byObjectId.GetValueOrDefault(objectId));
        }

        if (identifier.Address is { } address)
        {
            return Task.FromResult(_byAddress.GetValueOrDefault(address));
        }

        return Task.FromResult<ResolvedTenantMember?>(null);
    }
}
