namespace MultiChannelAgent.Domain.Inventories;

/// <summary>
/// A canonical Participant: a person from the owning organization's membership recognized identically
/// across every channel. Created idempotently the first time an active tenant member's Entra identity
/// is observed; <see cref="DisplayName"/> is refreshed from the latest authenticated claims.
/// </summary>
public sealed record Participant
{
    public required ParticipantId Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>
    /// Whether this Participant is currently known to be an active, resolvable, non-guest tenant
    /// member. Every newly (or freshly re-) observed sign-in sets this true; only an explicit tenant
    /// directory revalidation (see the recovery flow) ever sets it false. An Inventory is orphaned
    /// exactly when its sole Owner's <see cref="IsActive"/> is false - never merely because a
    /// Membership row happens to be missing.
    /// </summary>
    public required bool IsActive { get; init; }

    public static Participant Create(ParticipantId id, string? displayName)
    {
        return new Participant
        {
            Id = id,
            DisplayName = RequireNonBlank(displayName, nameof(displayName)),
            IsActive = true,
        };
    }

    private static string RequireNonBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be blank.", parameterName);
        }

        return value.Trim();
    }
}
