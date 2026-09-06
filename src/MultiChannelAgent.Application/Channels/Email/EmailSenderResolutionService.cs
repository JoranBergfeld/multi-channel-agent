using MultiChannelAgent.Application.Inventories;
using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Channels.Email;

/// <summary>
/// Resolves a verified internal mailbox address to the application-owned <see cref="ParticipantId"/>
/// behind it, or null when the directory does not resolve that sender to an active tenant member.
/// </summary>
public sealed class EmailSenderResolutionService(ITenantMemberDirectory tenantMemberDirectory)
{
    public async Task<ResolvedTenantMember?> ResolveMemberAsync(string senderAddress, CancellationToken cancellationToken)
    {
        var identifier = TenantMemberIdentifier.Parse(senderAddress);
        return identifier is null
            ? null
            : await tenantMemberDirectory.ResolveAsync(identifier, cancellationToken);
    }

    public async Task<ParticipantId?> ResolveAsync(string senderAddress, CancellationToken cancellationToken)
    {
        var resolved = await ResolveMemberAsync(senderAddress, cancellationToken);
        return resolved?.ParticipantId;
    }
}
