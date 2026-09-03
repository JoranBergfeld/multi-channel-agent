namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable, Data-Protection-protected payload for one server-side authentication session ticket,
/// referenced only by its opaque <see cref="Key"/> - the value the "mca_auth" browser cookie actually
/// carries. No claim, token, or other ticket content is ever readable from this row without the
/// application's Data Protection key ring: <see cref="ProtectedTicket"/> is opaque ciphertext, and it
/// is never logged or returned from any API.
/// </summary>
public sealed class AuthTicketEntity
{
    public required string Key { get; set; }

    public required byte[] ProtectedTicket { get; set; }

    /// <summary>Null when the underlying ticket carries no expiry; such a row is never eligible for expiry-based cleanup.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
