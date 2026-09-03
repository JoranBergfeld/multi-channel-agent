namespace MultiChannelAgent.Application.Inventories;

/// <summary>
/// Thrown by an <see cref="ITenantMemberDirectory"/> implementation when it cannot authoritatively
/// determine whether an identifier resolves - an authorization failure (misconfigured/expired
/// credentials, insufficient Graph permissions) or a transient failure (network error, timeout, or a
/// non-2xx Microsoft Graph response other than a definitive "not found"). This is deliberately never
/// used for an ordinary "no such member"/disabled/guest result - those are exactly and only a null
/// <see cref="ResolvedTenantMember"/> return. Callers must let this propagate rather than catching it
/// and treating the affected Participant as inactive/orphaned: a directory outage must surface as a
/// visible failure, never as a silent "member not found".
/// </summary>
public sealed class TenantDirectoryUnavailableException : Exception
{
    public TenantDirectoryUnavailableException(string message)
        : base(message)
    {
    }

    public TenantDirectoryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
