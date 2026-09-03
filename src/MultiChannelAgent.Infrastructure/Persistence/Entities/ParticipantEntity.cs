namespace MultiChannelAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// The durable row for one canonical Participant, upserted the first time (and every time) an active
/// tenant member's authenticated Entra identity is observed. <see cref="Id"/> equals the Participant's
/// immutable Entra object ID.
/// </summary>
public sealed class ParticipantEntity
{
    public Guid Id { get; set; }

    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
