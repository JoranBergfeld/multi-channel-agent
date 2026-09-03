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

    public static Participant Create(ParticipantId id, string? displayName)
    {
        return new Participant
        {
            Id = id,
            DisplayName = RequireNonBlank(displayName, nameof(displayName)),
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
