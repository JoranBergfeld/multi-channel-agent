using MultiChannelAgent.Domain.Inventories;

namespace MultiChannelAgent.Domain.Turns;

/// <summary>
/// The kind of authenticated evidence a channel adapter presents for the Participant behind a Turn.
/// It records how the channel established who this is, so trust decisions never have to infer it from
/// the channel name. Adapters added later contribute their own kinds rather than reusing a mismatched
/// one.
/// </summary>
public enum ChannelPrincipalKind
{
    /// <summary>An authenticated Microsoft Entra user (the signed-in web session today, Teams later).</summary>
    EntraUser,

    /// <summary>An authenticated internal Exchange user mailbox, resolved through the verified directory.</summary>
    ExchangeMailbox,
}

/// <summary>
/// The typed evidence one channel adapter presents for the Participant behind a Turn:
/// <see cref="Subject"/> is the channel's own authenticated subject (an Entra object id, a verified
/// mailbox address) and <see cref="TenantId"/> the directory it was authenticated against. This is
/// evidence, never authorization: the application resolves its own Participant identity and rechecks
/// Inventory access on every Turn regardless of what any adapter claims.
/// </summary>
public sealed record ChannelPrincipal
{
    public required ChannelPrincipalKind Kind { get; init; }

    public required string Subject { get; init; }

    public string? TenantId { get; init; }

    public static ChannelPrincipal EntraUser(string subject, string? tenantId) => new()
    {
        Kind = ChannelPrincipalKind.EntraUser,
        Subject = RequireNonBlank(subject, nameof(subject)),
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim(),
    };

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return value.Trim();
    }
}
