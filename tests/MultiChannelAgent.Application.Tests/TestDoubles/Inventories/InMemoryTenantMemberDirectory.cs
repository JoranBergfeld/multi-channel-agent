using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Application.Tests.TestDoubles.Inventories;

/// <summary>
/// Deterministic in-memory <see cref="ITenantMemberDirectory"/> double for Application-layer unit
/// tests: resolves only identities explicitly registered by a test, exactly as the real directory
/// would resolve only exact, active, non-guest tenant members - it never guesses a fuzzy match.
/// </summary>
public sealed class InMemoryTenantMemberDirectory : ITenantMemberDirectory
{
    private readonly Dictionary<Guid, ResolvedTenantMember> _byObjectId = [];
    private readonly Dictionary<string, ResolvedTenantMember> _byAddress = [];

    public void Register(ResolvedTenantMember member) => _byObjectId[member.ParticipantId.Value] = member;

    public void Register(string address, ResolvedTenantMember member) => _byAddress[address] = member;

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
