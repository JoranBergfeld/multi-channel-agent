using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Application.Inventories;

/// <summary>An active, resolvable, non-guest tenant member as returned by <see cref="ITenantMemberDirectory"/>.</summary>
public sealed record ResolvedTenantMember(ParticipantId ParticipantId, string DisplayName);

/// <summary>
/// A caller-supplied target identifier for membership grant/change and ownership transfer, resolved
/// to exactly one of an Entra object ID or a verified tenant address - never both, never a fuzzy
/// fragment. <see cref="Parse"/> is the sole, deterministic entry point: anything that is not an
/// exact GUID and does not look like an address returns null rather than guessing.
/// </summary>
public sealed record TenantMemberIdentifier
{
    public Guid? ObjectId { get; }

    public string? Address { get; }

    private TenantMemberIdentifier(Guid? objectId, string? address)
    {
        ObjectId = objectId;
        Address = address;
    }

    public static TenantMemberIdentifier? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();

        if (Guid.TryParse(trimmed, out var objectId))
        {
            return new TenantMemberIdentifier(objectId, null);
        }

        return trimmed.Contains('@', StringComparison.Ordinal)
            ? new TenantMemberIdentifier(null, trimmed)
            : null;
    }
}

/// <summary>
/// The deterministic application boundary to the owning organization's single-tenant Entra directory:
/// resolves an exact <see cref="TenantMemberIdentifier"/> to an active, non-guest tenant member, or
/// null when it does not resolve to one - never a fuzzy/best-effort match. Every membership grant,
/// ordinary ownership transfer, and recovery ownership transfer target is resolved through this same
/// seam, so "accept active tenant members only; exclude guests/external identities" holds in exactly
/// one place.
/// </summary>
public interface ITenantMemberDirectory
{
    Task<ResolvedTenantMember?> ResolveAsync(TenantMemberIdentifier identifier, CancellationToken cancellationToken);
}
