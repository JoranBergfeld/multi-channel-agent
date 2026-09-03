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

    /// <summary>
    /// Whether this Participant is currently known to be an active, resolvable, non-guest tenant
    /// member. Every sign-in sets this true; only an explicit tenant directory revalidation performed
    /// by the recovery flow ever sets it false. An Inventory is orphaned exactly when its sole
    /// Owner's <see cref="IsActive"/> is false.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
