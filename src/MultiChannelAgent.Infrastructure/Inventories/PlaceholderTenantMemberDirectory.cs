using MultiChannelAgent.Application.Inventories;

namespace MultiChannelAgent.Infrastructure.Inventories;

/// <summary>
/// The production <see cref="ITenantMemberDirectory"/> until real Microsoft Graph wiring lands: fails
/// fast and loudly rather than silently resolving nobody (which would make every grant/transfer/
/// recovery attempt look like "target not found" instead of surfacing the real gap) or guessing a
/// fuzzy match (which the directory boundary must never do). Web/application tests substitute a
/// deterministic directory double instead of exercising this adapter.
/// </summary>
public sealed class PlaceholderTenantMemberDirectory : ITenantMemberDirectory
{
    public Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "No production tenant member directory is wired yet (Microsoft Graph integration is a future ticket). " +
            "Configure a real ITenantMemberDirectory implementation before enabling membership grant, ownership " +
            "transfer, or recovery in a live environment.");
    }
}
