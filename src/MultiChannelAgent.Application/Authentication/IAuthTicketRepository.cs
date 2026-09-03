namespace MultiChannelAgent.Application.Authentication;

/// <summary>
/// Durable, provider-agnostic store for one server-side authentication ticket's Data-Protection-
/// protected byte payload, addressed only by an opaque session key. This is the sole persistence seam
/// behind ASP.NET Core cookie authentication's <c>SessionStore</c>: the browser's session cookie only
/// ever carries the key this repository returns/accepts, never the payload itself - so claims and any
/// embedded OIDC access/id/refresh token never reach client-observable storage. Implementations must
/// never log or otherwise expose <c>protectedTicket</c>.
/// </summary>
public interface IAuthTicketRepository
{
    /// <summary>
    /// Inserts a new row for <paramref name="key"/>, or overwrites the payload and expiry of an
    /// existing row for the same key in place (used both for the initial store and for renewal) -
    /// never creating a second row for the same key.
    /// </summary>
    Task SaveAsync(string key, byte[] protectedTicket, DateTimeOffset? expiresAtUtc, CancellationToken cancellationToken);

    /// <summary>Returns the protected payload for <paramref name="key"/>, or null when no row exists for it.</summary>
    Task<byte[]?> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>Removes the row for <paramref name="key"/>; a no-op (never throws) when no row exists for it.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
